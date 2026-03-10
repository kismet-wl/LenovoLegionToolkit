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
    private readonly NVAPIService _nvapiService;

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
    private int _lastProcessCount = -1;  // 用于检测进程数量变化

    // GPU 断电后暂停轮询标记
    private bool _isPausedDueToPowerOff = false;

    // 事件驱动刷新节流：防止短时间内多次刷新
    private DateTime _lastEventTriggeredRefresh = DateTime.MinValue;
    private readonly TimeSpan _eventRefreshThrottle = TimeSpan.FromSeconds(1);

    // 轮询间隔配置
    private const int DEFAULT_INTERVAL = 30_000; // 30秒作为兜底轮询
    private const int MONITOR_CONNECTED_INTERVAL = 60_000; // 独显直连时降低到60秒

    public event EventHandler<GPUStatus>? Refreshed;
    public bool IsStarted { get => _refreshTask is not null && !_refreshTask.IsCompleted; }

    public GPUController(NVAPIService nvapiService)
    {
        _nvapiService = nvapiService;
    }

    public bool IsSupported()
    {
        try
        {
            _nvapiService.Initialize();
            return _nvapiService.HasGPU();
        }
        catch
        {
            return false;
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
        // 确保 NVAPI 已初始化
        if (!_nvapiService.IsInitialized)
            _nvapiService.Initialize();

        try
        {
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (!_windowsMessageListener.IsMonitorOn)
                    return new GPUStatus(_state, _performanceState, _processes);

                await RefreshStateAsync().ConfigureAwait(false);

                // 触发事件，确保 UI 更新
                Refreshed?.Invoke(this, new GPUStatus(_state, _performanceState, _processes));

                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"RefreshNowAsync completed: state={_state}");

                return new GPUStatus(_state, _performanceState, _processes);
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"RefreshNowAsync failed", ex);

            return new GPUStatus(_state, _performanceState, _processes);
        }
    }

    public Task StartAsync(int delay = 1_000, int interval = DEFAULT_INTERVAL)
    {
        _isPausedDueToPowerOff = false;  // 重置暂停状态

        if (IsStarted)
            return Task.CompletedTask;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Starting... [delay={delay}, interval={interval}]");

        _windowsMessageListener.MonitorStateChanged += MonitorStateChanged;
        _windowsMessageListener.Changed += OnWindowsMessageChanged;
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
        _windowsMessageListener.Changed -= OnWindowsMessageChanged;
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
                Log.Instance.Trace($"Initializing NVAPI via NVAPIService...");

            _nvapiService.Initialize();

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"NVAPI initialized via NVAPIService");

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

                        // 每次刷新都触发事件，确保 UI 持续更新
                        if (Log.Instance.IsTraceEnabled)
                            Log.Instance.Trace($"GPU state: {_state}, stateChanged={stateChanged}");

                        Refreshed?.Invoke(this, new GPUStatus(_state, _performanceState, _processes));

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

                // 根据当前状态动态调整轮询间隔
                var currentInterval = _state == GPUState.MonitorConnected 
                    ? MONITOR_CONNECTED_INTERVAL  // 独显直连时降低频率
                    : interval;

                if (currentInterval > 0)
                {
                    var delayTask = Task.Delay(currentInterval, token);
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
    }

    private async Task<bool> RefreshStateAsync()
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Refresh in progress...");

        _state = GPUState.Unknown;
        _processes = [];
        _gpuInstanceId = null;
        _performanceState = null;

        var gpu = _nvapiService.GetGPU();
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

        // 检测状态变化（包括进程数量变化）
        var stateChanged = _state != _lastState || _performanceState != _lastPerformanceState;
        var processCountChanged = _processes.Count != _lastProcessCount;
        
        _lastState = _state;
        _lastPerformanceState = _performanceState;
        _lastProcessCount = _processes.Count;

        return stateChanged || processCountChanged;
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

    private void OnWindowsMessageChanged(object? sender, NativeWindowsMessageListener.ChangedEventArgs e)
    {
        if (!IsStarted)
            return;

        // 只处理显示设备相关的事件
        if (e.Message != NativeWindowsMessage.OnDisplayDeviceArrival &&
            e.Message != NativeWindowsMessage.MonitorConnected &&
            e.Message != NativeWindowsMessage.MonitorDisconnected &&
            e.Message != NativeWindowsMessage.ExternalMonitorConnected &&
            e.Message != NativeWindowsMessage.ExternalMonitorDisconnected)
            return;

        // 节流：防止短时间内多次刷新
        var now = DateTime.Now;
        if (now - _lastEventTriggeredRefresh < _eventRefreshThrottle)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Event {e.Message} throttled, skipping refresh");
            return;
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Event {e.Message} received, triggering GPU state refresh");

        _lastEventTriggeredRefresh = now;
        _ = TriggerRefreshAsync();
    }

    /// <summary>
    /// 事件触发的异步刷新，不阻塞调用线程
    /// </summary>
    private async Task TriggerRefreshAsync()
    {
        // 确保 NVAPI 已初始化
        if (!_nvapiService.IsInitialized)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Event-triggered refresh skipped: NVAPI not initialized");
            return;
        }

        try
        {
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                if (!_windowsMessageListener.IsMonitorOn)
                    return;

                var stateChanged = await RefreshStateAsync().ConfigureAwait(false);

                if (stateChanged)
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"Event-triggered refresh: state changed to {_state}");

                    Refreshed?.Invoke(this, new GPUStatus(_state, _performanceState, _processes));
                }
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Event-triggered refresh failed", ex);
        }
    }
}
