﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acr.UserDialogs;
using BLE.Client.Extensions;
using MvvmCross.Core.ViewModels;
using MvvmCross.Platform;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Extensions;
using Plugin.Settings.Abstractions;

namespace BLE.Client.ViewModels
{
    public class DeviceListViewModel : BaseViewModel
    {
        private readonly IBluetoothLE _bluetoothLe;
        private readonly IUserDialogs _userDialogs;
        private readonly ISettings _settings;
        private Guid _previousGuid;
        private CancellationTokenSource _cancellationTokenSource;

        public Guid PreviousGuid
        {
            get { return _previousGuid; }
            set
            {
                _previousGuid = value;
                _settings.AddOrUpdateValue("lastguid", _previousGuid.ToString());
                RaisePropertyChanged();
                RaisePropertyChanged(() => ConnectToPreviousCommand);
            }
        }

        public MvxCommand RefreshCommand => new MvxCommand(() => TryStartScanning(true));
        public MvxCommand<DeviceListItemViewModel> DisconnectCommand => new MvxCommand<DeviceListItemViewModel>(DisconnectDevice);

        public MvxCommand<DeviceListItemViewModel> ConnectDisposeCommand => new MvxCommand<DeviceListItemViewModel>(ConnectAndDisposeDevice);

        public ObservableCollection<DeviceListItemViewModel> Devices { get; set; } = new ObservableCollection<DeviceListItemViewModel>();
        public bool IsRefreshing => Adapter.IsScanning;
        public bool IsStateOn => _bluetoothLe.IsOn;
        public string StateText => GetStateText();
        public DeviceListItemViewModel SelectedDevice
        {
            get { return null; }
            set
            {
                if (value != null)
                {
                    HandleSelectedDevice(value);
                }

                RaisePropertyChanged();
            }
        }

        public MvxCommand StopScanCommand => new MvxCommand(() =>
        {
            _cancellationTokenSource.Cancel();
            CleanupCancellationToken();
            RaisePropertyChanged(() => IsRefreshing);
        }, () => _cancellationTokenSource != null);

        public DeviceListViewModel(IBluetoothLE bluetoothLe, IAdapter adapter, IUserDialogs userDialogs, ISettings settings) : base(adapter)
        {
            _bluetoothLe = bluetoothLe;
            _userDialogs = userDialogs;
            _settings = settings;
            // quick and dirty :>
            _bluetoothLe.StateChanged += OnStateChanged;
            Adapter.DeviceDiscovered += OnDeviceDiscovered;
            Adapter.ScanTimeoutElapsed += Adapter_ScanTimeoutElapsed;
            Adapter.DeviceDisconnected += OnDeviceDisconnected;
            Adapter.DeviceConnectionLost += OnDeviceConnectionLost;
        }

        private Task GetPreviousGuidAsync()
        {
            return Task.Run(() =>
            {
                var guidString = _settings.GetValueOrDefault<string>("lastguid", null);
                PreviousGuid = !string.IsNullOrEmpty(guidString) ? Guid.Parse(guidString) : Guid.Empty;
            });
        }

        private void OnDeviceConnectionLost(object sender, DeviceErrorEventArgs e)
        {
            Devices.FirstOrDefault(d => d.Id == e.Device.Id)?.Update();

            _userDialogs.HideLoading();
            _userDialogs.ErrorToast("Error", $"Connection LOST {e.Device.Name}", TimeSpan.FromMilliseconds(6000));
        }

        private void OnStateChanged(object sender, BluetoothStateChangedArgs e)
        {
            RaisePropertyChanged(nameof(IsStateOn));
            RaisePropertyChanged(nameof(StateText));
            //TryStartScanning();
        }

        private string GetStateText()
        {
            switch (_bluetoothLe.State)
            {
                case BluetoothState.Unknown:
                    return "Unknown BLE state.";
                case BluetoothState.Unavailable:
                    return "BLE is not available on this device.";
                case BluetoothState.Unauthorized:
                    return "You are not allowed to use BLE.";
                case BluetoothState.TurningOn:
                    return "BLE is warming up, please wait.";
                case BluetoothState.On:
                    return "BLE is on.";
                case BluetoothState.TurningOff:
                    return "BLE is turning off. That's sad!";
                case BluetoothState.Off:
                    return "BLE is off. Turn it on!";
                default:
                    return "Unknown BLE state.";
            }
        }

        private void Adapter_ScanTimeoutElapsed(object sender, EventArgs e)
        {
            RaisePropertyChanged(() => IsRefreshing);

            CleanupCancellationToken();
        }

        private void OnDeviceDiscovered(object sender, DeviceEventArgs args)
        {
            AddOrUpdateDevice(args.Device);
        }

        private void AddOrUpdateDevice(IDevice device)
        {
            InvokeOnMainThread(() =>
            {
                var vm = Devices.FirstOrDefault(d => d.Device.Id == device.Id);
                if (vm != null)
                {
                    vm.Update();
                }
                else
                {
                    Devices.Add(new DeviceListItemViewModel(device));
                }
            });
        }

        public override async void Resume()
        {
            base.Resume();

            await GetPreviousGuidAsync();
            //TryStartScanning();

            try
            {
                SystemDevices = Adapter.GetSystemConnectedDevices().Select(d => new DeviceListItemViewModel(d)).ToList();
                RaisePropertyChanged(() => SystemDevices);
            }
            catch (Exception ex)
            {
                Trace.Message("Failed to retreive system connected devices. {0}", ex.Message);
            }
        }

        public List<DeviceListItemViewModel> SystemDevices { get; private set; }

        public override void Suspend()
        {
            base.Suspend();

            Adapter.StopScanningForDevicesAsync();
            RaisePropertyChanged(() => IsRefreshing);
        }

        private void TryStartScanning(bool refresh = false)
        {
            if (IsStateOn && (refresh || !Devices.Any()) && !IsRefreshing)
            {
                ScanForDevices();
            }
        }

        private async void ScanForDevices()
        {
            Devices.Clear();

            foreach (var connectedDevice in Adapter.ConnectedDevices)
            {
                //update rssi for already connected evices (so tha 0 is not shown in the list)
                try
                {
                    await connectedDevice.UpdateRssiAsync();
                }
                catch (Exception ex)
                {
                    Mvx.Trace(ex.Message);
                    _userDialogs.ShowError($"Failed to update RSSI for {connectedDevice.Name}");
                }

                AddOrUpdateDevice(connectedDevice);
            }

            _cancellationTokenSource = new CancellationTokenSource();
            RaisePropertyChanged(() => StopScanCommand);

            Adapter.StartScanningForDevicesAsync(_cancellationTokenSource.Token);
            RaisePropertyChanged(() => IsRefreshing);
        }

        private void CleanupCancellationToken()
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            RaisePropertyChanged(() => StopScanCommand);
        }

        private async void DisconnectDevice(DeviceListItemViewModel device)
        {
            try
            {
                if (!device.IsConnected)
                    return;

                _userDialogs.ShowLoading($"Disconnecting {device.Name}...");

                await Adapter.DisconnectDeviceAsync(device.Device);
            }
            catch (Exception ex)
            {
                _userDialogs.Alert(ex.Message, "Disconnect error");
            }
            finally
            {
                device.Update();
                _userDialogs.HideLoading();
            }
        }

        private async void HandleSelectedDevice(DeviceListItemViewModel device)
        {
            if (await ConnectDeviceAsync(device))
            {
                ShowViewModel<ServiceListViewModel>(new MvxBundle(new Dictionary<string, string> { { DeviceIdKey, device.Device.Id.ToString() } }));
            }
        }

        private async Task<bool> ConnectDeviceAsync(DeviceListItemViewModel device, bool showPrompt = true)
        {
            //if (device.IsConnected)
            //{
            //    return true;
            //}

            if (showPrompt && !await _userDialogs.ConfirmAsync($"Connect to device '{device.Name}'?"))
            {
                return false;
            }
            try
            {
                CancellationTokenSource tokenSource = new CancellationTokenSource();

                var config = new ProgressDialogConfig()
                {
                    Title = $"Connecting to '{device.Id}'",
                    CancelText = "Cancel",
                    IsDeterministic = false,
                    OnCancel = tokenSource.Cancel
                };

                using (var progress = _userDialogs.Progress(config))
                {
                    progress.Show();

                    await Adapter.ConnectToDeviceAsync(device.Device, tokenSource.Token);
                }

                _userDialogs.ShowSuccess($"Connected to {device.Device.Name}.");

                PreviousGuid = device.Device.Id;
                return true;

            }
            catch (Exception ex)
            {
                _userDialogs.Alert(ex.Message, "Connection error");
                Mvx.Trace(ex.Message);
                return false;
            }
            finally
            {
                _userDialogs.HideLoading();
                device.Update();
            }
        }


        public MvxCommand ConnectToPreviousCommand => new MvxCommand(ConnectToPreviousDeviceAsync, CanConnectToPrevious);

        private async void ConnectToPreviousDeviceAsync()
        {
            IDevice device;
            try
            {
                CancellationTokenSource tokenSource = new CancellationTokenSource();

                var config = new ProgressDialogConfig()
                {
                    Title = $"Searching for '{PreviousGuid}'",
                    CancelText = "Cancel",
                    IsDeterministic = false,
                    OnCancel = tokenSource.Cancel
                };

                using (var progress = _userDialogs.Progress(config))
                {
                    progress.Show();

                    device = await Adapter.ConnectToKnownDeviceAsync(PreviousGuid);

                }

                _userDialogs.ShowSuccess($"Connected to {device.Name}.");

                var deviceItem = Devices.FirstOrDefault(d => d.Device.Id == device.Id);
                if (deviceItem == null)
                {
                    deviceItem = new DeviceListItemViewModel(device);
                    Devices.Add(deviceItem);
                }
                else
                {
                    deviceItem.Update(device);
                }
            }
            catch (Exception ex)
            {
                _userDialogs.ShowError(ex.Message, 5000);
                return;
            }
        }

        private bool CanConnectToPrevious()
        {
            return PreviousGuid != default(Guid);
        }

        private async void ConnectAndDisposeDevice(DeviceListItemViewModel item)
        {
            try
            {
                using (item.Device)
                {
                    _userDialogs.ShowLoading($"Connecting to {item.Name} ...");
                    await Adapter.ConnectToDeviceAsync(item.Device);
                    item.Update();
                    _userDialogs.ShowSuccess($"Connected {item.Device.Name}");

                    _userDialogs.HideLoading();
                    for (var i = 5; i >= 1; i--)
                    {
                        _userDialogs.ShowLoading($"Disconnect in {i}s...");

                        await Task.Delay(1000);

                        _userDialogs.HideLoading();
                    }
                }
            }
            catch (Exception ex)
            {
                _userDialogs.Alert(ex.Message, "Failed to connect and dispose.");
            }
            finally
            {
                _userDialogs.HideLoading();
            }


        }

        private void OnDeviceDisconnected(object sender, DeviceEventArgs e)
        {
            Devices.FirstOrDefault(d => d.Id == e.Device.Id)?.Update();
            _userDialogs.HideLoading();
            _userDialogs.Toast($"Disconnected {e.Device.Name}");
        }

        public MvxCommand<DeviceListItemViewModel> CopyGuidCommand => new MvxCommand<DeviceListItemViewModel>(device =>
        {
            PreviousGuid = device.Id;
        });
    }
}