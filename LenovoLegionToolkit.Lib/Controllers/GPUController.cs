using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Resources;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.System.Management;
using LenovoLegionToolkit.Lib.Utils;
using NeoSmart.AsyncLock;

namespace LenovoLegionToolkit.Lib.Controllers;

public class GPUController
{
    private readonly AsyncLock _lock = new();
    private readonly NativeWindowsMessageListener _windowsMessageListener = IoCContainer.Resolve<NativeWindowsMessageListener>();

    private Task? _refreshTask;
    private CancellationTokenSource? _refreshCancellationTokenSource;
    private CancellationTokenSource? _monitorStateCancellationTokenSource;

    private GPUState _state = GPUState.Unknown;
    private List<Process> _processes = [];
    private string? _gpuInstanceId;
    private string? _performanceState;

    // 用于检测状态变化
    private GPUState _lastState = GPUState.Unknown;
    private string? _lastPerformanceState;

    // GPU 断电后暂停轮询标记
    private bool _isPausedDueToPowerOff = false;

    public event EventHandler<GPUStatus>? Refreshed;
    public bool IsStarted { get => _refreshTask is not null && !_refreshTask.IsCompleted; }

    public bool IsSupported()
    {
        try
        {
            NVAPI.Initialize();
            return NVAPI.GetGPU() is not null;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                NVAPI.Unload();
            }
            catch { /* Ignored. */ }
        }
    }

    public async Task<GPUState> GetLastKnownStateAsync()
    {
        using (await _lock.LockAsync().ConfigureAwait(false))
            return _state;
    }

    public async Task<GPUStatus> GetLastKnownStatusAsync()
    {
        using (await _lock.LockAsync().ConfigureAwait(false))
            return new GPUStatus(_state, _performanceState, _processes);
    }

    public async Task<GPUStatus> RefreshNowAsync()
    {
        using (await _lock.LockAsync().ConfigureAwait(false))
        {
            await RefreshLoopAsync(0, 0, CancellationToken.None).ConfigureAwait(false);
            return new GPUStatus(_state, _performanceState, _processes);
        }
    }

    public Task StartAsync(int delay = 1_000, int interval = 5_000)
    {
        _isPausedDueToPowerOff = false;  // 重置暂停状态

        if (IsStarted)
            return Task.CompletedTask;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Starting... [delay={delay}, interval={interval}]");

        _windowsMessageListener.MonitorStateChanged += MonitorStateChanged;
        _refreshCancellationTokenSource = new CancellationTokenSource();
        var token = _refreshCancellationTokenSource.Token;
        _refreshTask = Task.Run(() => RefreshLoopAsync(delay, interval, token), token);
        return Task.CompletedTask;
    }

    public async Task ResumeIfPausedAsync()
    {
        // 清理已完成的任务，允许重新启动
        if (_refreshTask is not null && _refreshTask.IsCompleted)
        {
            _refreshTask = null;
            _refreshCancellationTokenSource = null;
        }

        if (!_isPausedDueToPowerOff)
            return;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Resuming from power-off pause...");

        _isPausedDueToPowerOff = false;
        _lastState = GPUState.Unknown;  // 重置状态，确保下次刷新会触发事件
        await StartAsync();
    }

    public async Task StopAsync(bool waitForFinish = false)
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Stopping... [refreshTask.isNull={_refreshTask is null}, _refreshCancellationTokenSource.IsCancellationRequested={_refreshCancellationTokenSource?.IsCancellationRequested}]");

        if (_refreshCancellationTokenSource is not null)
            await _refreshCancellationTokenSource.CancelAsync().ConfigureAwait(false);

        if (waitForFinish)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Waiting to finish...");

            if (_refreshTask is not null)
            {
                try
                {
                    await _refreshTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            }

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Finished");
        }

        _refreshCancellationTokenSource = null;
        _refreshTask = null;

        _windowsMessageListener.MonitorStateChanged -= MonitorStateChanged;
        _monitorStateCancellationTokenSource?.Cancel();
        _monitorStateCancellationTokenSource = null;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Stopped");
    }

    public async Task RestartGPUAsync()
    {
        using (await _lock.LockAsync().ConfigureAwait(false))
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Deactivating... [state={_state}, gpuInstanceId={_gpuInstanceId}]");

            if (_state is not GPUState.Active and not GPUState.Inactive)
                return;

            if (string.IsNullOrEmpty(_gpuInstanceId))
                return;

            await CMD.RunAsync("pnputil", $"/restart-device \"{_gpuInstanceId}\"").ConfigureAwait(false);

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Deactivating... [state= {_state}, gpuInstanceId={_gpuInstanceId}]");
        }
    }

    public async Task KillGPUProcessesAsync()
    {
        using (await _lock.LockAsync().ConfigureAwait(false))
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Deactivating... [state= {_state}, gpuInstanceId={_gpuInstanceId}]");

            if (_state is not GPUState.Active)
                return;

            if (string.IsNullOrEmpty(_gpuInstanceId))
                return;

            foreach (var process in _processes)
            {
                try
                {
                    process.Kill(true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"Couldn't kill process. [pid={process.Id}, name={process.ProcessName}]", ex);
                }
            }

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Deactivating... [state=  {_state}, gpuInstanceId={_gpuInstanceId}]");
        }
    }

    private async Task RefreshLoopAsync(int delay, int interval, CancellationToken token)
    {
        try
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Initializing NVAPI...");

            NVAPI.Initialize();

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Initialized NVAPI");

            await Task.Delay(delay, token).ConfigureAwait(false);

            while (true)
            {
                token.ThrowIfCancellationRequested();

                using (await _lock.LockAsync(token).ConfigureAwait(false))
                {
                    // 只在显示器开启时刷新 GPU 状态
                    if (_windowsMessageListener.IsMonitorOn)
                    {
                        if (Log.Instance.IsTraceEnabled)
                            Log.Instance.Trace($"Will refresh...");

                        var stateChanged = await RefreshStateAsync().ConfigureAwait(false);

                        if (Log.Instance.IsTraceEnabled)
                            Log.Instance.Trace($"Refreshed, stateChanged={stateChanged}");

                        // 只在状态变化时触发事件
                        if (stateChanged)
                        {
                            if (Log.Instance.IsTraceEnabled)
                                Log.Instance.Trace($"GPU state changed: {_state}");

                            Refreshed?.Invoke(this, new GPUStatus(_state, _performanceState, _processes));
                        }

                        // GPU 断电后停止轮询，避免唤醒 GPU
                        if (_state == GPUState.PoweredOff)
                        {
                            _isPausedDueToPowerOff = true;
                            if (Log.Instance.IsTraceEnabled)
                                Log.Instance.Trace($"GPU powered off, stopping polling to avoid waking GPU");
                            return;
                        }
                    }
                }

if (interval > 0)
                {
                    var delayTask = Task.Delay(interval, token);
                    if (_monitorStateCancellationTokenSource?.IsCancellationRequested == false)
                    {
                        // 等待延迟或显示器状态变化
                        var monitorStateTask = Task.Run(async () =>
                        {
                            _monitorStateCancellationTokenSource.Token.WaitHandle.WaitOne();
                        }, _monitorStateCancellationTokenSource.Token);
                        
                        await Task.WhenAny(delayTask, monitorStateTask).ConfigureAwait(false);
                    }
                    else
                    {
                        await delayTask.ConfigureAwait(false);
                    }
                }
                else
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Exception occurred", ex);

            throw;
        }
        finally
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Unloading NVAPI...");

            NVAPI.Unload();

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Unloaded NVAPI");
        }
    }

    private async Task<bool> RefreshStateAsync()
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Refresh in progress...");

        _state = GPUState.Unknown;
        _processes = [];
        _gpuInstanceId = null;
        _performanceState = null;

        var gpu = NVAPI.GetGPU();
        if (gpu is null)
        {
            _state = GPUState.NvidiaGpuNotFound;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"GPU present [state={_state}, processes.Count={_processes.Count}, gpuInstanceId={_gpuInstanceId}]");

            var changed = _state != _lastState;
            _lastState = _state;
            return changed;
        }

        try
        {
            var stateId = gpu.PerformanceStatesInfo.CurrentPerformanceState.StateId.ToString().GetUntilOrEmpty("_");
            _performanceState = Resource.GPUController_PoweredOn;
            if (!string.IsNullOrWhiteSpace(stateId))
                _performanceState += $", {stateId}";
        }
        catch (Exception ex) when (ex.Message == "NVAPI_GPU_NOT_POWERED")
        {
            _state = GPUState.PoweredOff;
            _performanceState = Resource.GPUController_PoweredOff;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Powered off [state={_state}, processes.Count={_processes.Count}, gpuInstanceId={_gpuInstanceId}]");

            var changed = _state != _lastState || _performanceState != _lastPerformanceState;
            _lastState = _state;
            _lastPerformanceState = _performanceState;
            return changed;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"GPU status exception.", ex);

            _performanceState = "Unknown";
        }

        var pnpDeviceIdPart = NVAPI.GetGPUId(gpu);

        if (string.IsNullOrEmpty(pnpDeviceIdPart))
            throw new InvalidOperationException("pnpDeviceIdPart is null or empty");

        var gpuInstanceId = await WMI.Win32.PnpEntity.GetDeviceIDAsync(pnpDeviceIdPart).ConfigureAwait(false);
        var processNames = NVAPIExtensions.GetActiveProcesses(gpu);

        if (NVAPI.IsDisplayConnected(gpu))
        {
            _processes = processNames;
            _state = GPUState.MonitorConnected;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace(
                    $"Monitor connected [state={_state}, processes.Count={_processes.Count}, gpuInstanceId={_gpuInstanceId}]");
        }
        else if (processNames.Count != 0)
        {
            _processes = processNames;
            _state = GPUState.Active;
            _gpuInstanceId = gpuInstanceId;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Active [state={_state}, processes.Count={_processes.Count}, gpuInstanceId={_gpuInstanceId}, pnpDeviceIdPart={pnpDeviceIdPart}]");
        }
        else
        {
            _state = GPUState.Inactive;
            _gpuInstanceId = gpuInstanceId;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Inactive [state={_state}, processes.Count={_processes.Count}, gpuInstanceId={_gpuInstanceId}]");
        }

        // 检测状态变化
        var stateChanged = _state != _lastState || _performanceState != _lastPerformanceState;
        _lastState = _state;
        _lastPerformanceState = _performanceState;

        return stateChanged;
    }

    private void MonitorStateChanged(object? sender, bool isMonitorOn)
    {
        if (!IsStarted)
            return;

        if (isMonitorOn)
        {
            // 显示器开启，如果被暂停则恢复
            if (_monitorStateCancellationTokenSource?.IsCancellationRequested == true)
            {
                _monitorStateCancellationTokenSource = new();
            }
        }
        else
        {
            // 显示器关闭，暂停刷新
            _monitorStateCancellationTokenSource?.Cancel();
        }
    }
}
