using HandheldCompanion.Controls;
using HandheldCompanion.Misc;
using HandheldCompanion.Processors;
using HandheldCompanion.Views;
using RTSSSharedMemoryNET;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using Timer = System.Timers.Timer;

namespace HandheldCompanion.Managers;

public static class OSPowerMode
{
    /// <summary>
    ///     Better Battery mode.
    /// </summary>
    public static Guid BetterBattery = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    /// <summary>
    ///     Better Performance mode.
    /// </summary>
    // public static Guid BetterPerformance = new Guid("3af9B8d9-7c97-431d-ad78-34a8bfea439f");
    public static Guid BetterPerformance = new();

    /// <summary>
    ///     Best Performance mode.
    /// </summary>
    public static Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
}

public static class PerformanceManager
{
    private const short INTERVAL_DEFAULT = 3000; // default interval between value scans
    private const short INTERVAL_AUTO = 1010; // default interval between value scans
    private const short INTERVAL_DEGRADED = 5000; // degraded interval between value scans
    public static int MaxDegreeOfParallelism = 4;

    public static readonly Guid[] PowerModes = new Guid[3] { OSPowerMode.BetterBattery, OSPowerMode.BetterPerformance, OSPowerMode.BestPerformance };

    private static readonly Timer autoWatchdog;
    private static readonly Timer cpuWatchdog;
    private static readonly Timer gfxWatchdog;
    private static readonly Timer powerWatchdog;

    private static bool autoLock;
    private static bool cpuLock;
    private static bool gfxLock;
    private static bool powerLock;

    // AutoTDP
    private static double AutoTDP;
    private static double AutoTDPPrev;
    private static bool AutoTDPFirstRun = true;
    private static int AutoTDPFPSSetpointMetCounter;
    private static int AutoTDPFPSSmallDipCounter;
    private static double AutoTDPMax;
    private static double TDPMax;
    private static double TDPMin;
    private static int AutoTDPProcessId;
    private static double AutoTDPTargetFPS;
    private static bool cpuWatchdogPendingStop;
    private static uint currentEPP = 50;
    private static int currentCoreCount;
    private static double CurrentGfxClock;

    // powercfg
    private static bool currentPerfBoostMode;
    private static Guid currentPowerMode = new("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
    private static readonly double[] CurrentTDP = new double[5]; // used to store current TDP

    // GPU limits
    private static double FallbackGfxClock;
    private static readonly double[] FPSHistory = new double[6];
    private static bool gfxWatchdogPendingStop;

    private static Processor processor = new();
    private static double ProcessValueFPSPrevious;
    private static double StoredGfxClock;

    // TDP limits
    private static readonly double[] StoredTDP = new double[3]; // used to store TDP

    private static bool IsInitialized;

    public static event InitializedEventHandler Initialized;
    public delegate void InitializedEventHandler();

    static PerformanceManager()
    {
        // initialize timer(s)
        powerWatchdog = new Timer { Interval = INTERVAL_DEFAULT, AutoReset = true, Enabled = false };
        powerWatchdog.Elapsed += powerWatchdog_Elapsed;

        cpuWatchdog = new Timer { Interval = INTERVAL_DEFAULT, AutoReset = true, Enabled = false };
        cpuWatchdog.Elapsed += cpuWatchdog_Elapsed;

        gfxWatchdog = new Timer { Interval = INTERVAL_DEFAULT, AutoReset = true, Enabled = false };
        gfxWatchdog.Elapsed += gfxWatchdog_Elapsed;

        autoWatchdog = new Timer { Interval = INTERVAL_AUTO, AutoReset = true, Enabled = false };
        autoWatchdog.Elapsed += AutoTDPWatchdog_Elapsed;

        // manage events
        ProfileManager.Applied += ProfileManager_Applied;
        ProfileManager.Discarded += ProfileManager_Discarded;
        ProfileManager.Updated += ProfileManager_Updated;
        PowerProfileManager.Applied += PowerProfileManager_Applied;
        PowerProfileManager.Discarded += PowerProfileManager_Discarded;
        PlatformManager.LibreHardwareMonitor.GPUClockChanged += LibreHardwareMonitor_GPUClockChanged;
        PlatformManager.RTSS.Hooked += RTSS_Hooked;
        PlatformManager.RTSS.Unhooked += RTSS_Unhooked;
        SettingsManager.SettingValueChanged += SettingsManagerOnSettingValueChanged;
        HotkeysManager.CommandExecuted += HotkeysManager_CommandExecuted;

        // move me
        SystemManager.StateChanged_RSR += SystemManager_StateChanged_RSR;
        SystemManager.StateChanged_IntegerScaling += SystemManager_StateChanged_IntegerScaling;
        SystemManager.StateChanged_ImageSharpening += SystemManager_StateChanged_ImageSharpening;
        SystemManager.StateChanged_GPUScaling += SystemManager_StateChanged_GPUScaling;

        currentCoreCount = Environment.ProcessorCount;
        MaxDegreeOfParallelism = Convert.ToInt32(Environment.ProcessorCount / 2);
    }

    private static void SettingsManagerOnSettingValueChanged(string name, object value)
    {
        switch (name)
        {
            case "ConfigurableTDPOverrideDown":
                {
                    TDPMin = Convert.ToDouble(value);
                    AutoTDP = (TDPMax + TDPMin) / 2.0d;
                }
                break;
            case "ConfigurableTDPOverrideUp":
                {
                    TDPMax = Convert.ToDouble(value);
                    if (AutoTDPMax == 0d) AutoTDPMax = TDPMax;
                    AutoTDP = (TDPMax + TDPMin) / 2.0d;
                }
                break;
        }
    }

    private static void HotkeysManager_CommandExecuted(string listener)
    {
        PowerProfile powerProfile = PowerProfileManager.GetCurrent();
        if (powerProfile is null)
            return;

        switch (listener)
        {
            case "increaseTDP":
                {
                    if (powerProfile.TDPOverrideEnabled)
                        return;

                    for (int idx = (int)PowerType.Slow; idx <= (int)PowerType.Fast; idx++)
                        powerProfile.TDPOverrideValues[idx] = Math.Min(TDPMax, powerProfile.TDPOverrideValues[idx] + 1);

                    PowerProfileManager.UpdateOrCreateProfile(powerProfile, UpdateSource.Background);
                }
                break;
            case "decreaseTDP":
                {
                    if (powerProfile.TDPOverrideEnabled)
                        return;

                    for (int idx = (int)PowerType.Slow; idx <= (int)PowerType.Fast; idx++)
                        powerProfile.TDPOverrideValues[idx] = Math.Max(TDPMin, powerProfile.TDPOverrideValues[idx] - 1);

                    PowerProfileManager.UpdateOrCreateProfile(powerProfile, UpdateSource.Background);
                }
                break;
        }
    }

    private static void ProfileManager_Applied(Profile profile, UpdateSource source)
    {
        try
        {
            // apply profile GPU Scaling
            // apply profile scaling mode
            if (profile.GPUScaling)
            {
                ADLXWrapper.SetGPUScaling(true);

                var scalingMode = profile.ScalingMode;
                 
                // RSR + ScalingMode.Center not supported (stop applying center if it's somehow on a profile)
                // Technically shouldn't occur and be stopped from UI, but could potentially be possible from older versions
                if (profile.ScalingMode == 2 && profile.RSREnabled)
                    scalingMode = 1;

                ADLXWrapper.SetScalingMode(scalingMode);

                // apply profile RSR
                if (profile.RSREnabled)
                {
                    // mutually exclusive
                    ADLXWrapper.SetIntegerScaling(false);
                    ADLXWrapper.SetImageSharpening(false);

                    ADLXWrapper.SetRSR(true);
                    ADLXWrapper.SetRSRSharpness(profile.RSRSharpness);
                }
                else if (ADLXWrapper.GetRSR())
                {
                    ADLXWrapper.SetRSR(false);
                    ADLXWrapper.SetRSRSharpness(20);
                }

                // apply profile Integer Scaling
                if (profile.IntegerScalingEnabled)
                {
                    // mutually exclusive
                    ADLXWrapper.SetRSR(false);

                    ADLXWrapper.SetIntegerScaling(true);
                }
                else if (ADLXWrapper.GetIntegerScaling())
                {
                    ADLXWrapper.SetIntegerScaling(false);
                }
            }
            else if (ADLXWrapper.GetGPUScaling())
            {
                ADLXWrapper.SetGPUScaling(false);
            }

            // apply profile image sharpening
            if (profile.RISEnabled)
            {
                // mutually exclusive
                ADLXWrapper.SetRSR(false);

                ADLXWrapper.SetImageSharpening(profile.RISEnabled);
                ADLXWrapper.SetImageSharpeningSharpness(profile.RISSharpness);
            }
            else if (ADLXWrapper.GetImageSharpening())
            {
                ADLXWrapper.SetImageSharpening(false);
            }
        }
        catch { }
    }

    private static void ProfileManager_Discarded(Profile profile)
    {
        try
        {
            // restore default GPU Scaling
            if (profile.GPUScaling && ADLXWrapper.GetGPUScaling())
            {
                ADLXWrapper.SetGPUScaling(false);
            }

            // restore default RSR
            if (profile.RSREnabled && ADLXWrapper.GetRSR())
            {
                ADLXWrapper.SetRSR(false);
                ADLXWrapper.SetRSRSharpness(20);
            }
            
            // restore default integer scaling
            if (profile.IntegerScalingEnabled && ADLXWrapper.GetIntegerScaling())
            {
                ADLXWrapper.SetIntegerScaling(false);
            }

            // restore default image sharpening
            if (profile.RISEnabled && ADLXWrapper.GetImageSharpening())
            {
                ADLXWrapper.SetImageSharpening(false);
            }
        }
        catch { }
    }
    
    // todo: moveme
    private static void ProfileManager_Updated(Profile profile, UpdateSource source, bool isCurrent)
    {
        ProcessEx.SetAppCompatFlag(profile.Path, ProcessEx.DisabledMaximizedWindowedValue, !profile.FullScreenOptimization);
        ProcessEx.SetAppCompatFlag(profile.Path, ProcessEx.HighDPIAwareValue, !profile.HighDPIAware);
    }

    private static void SystemManager_StateChanged_RSR(bool Supported, bool Enabled, int Sharpness)
    {
        Profile profile = ProfileManager.GetCurrent();
        if (Enabled != profile.RSREnabled)
            ADLXWrapper.SetRSR(profile.RSREnabled);
        if (Sharpness != profile.RSRSharpness)
            ADLXWrapper.SetRSRSharpness(profile.RSRSharpness);
    }

    private static void SystemManager_StateChanged_GPUScaling(bool Supported, bool Enabled, int Mode)
    {
        Profile profile = ProfileManager.GetCurrent();
        if (Enabled != profile.GPUScaling)
            ADLXWrapper.SetGPUScaling(profile.GPUScaling);
        if (Mode != profile.ScalingMode)
        {
            // RSR + ScalingMode.Center not supported (stop applying center if it's somehow on a profile)
            // Technically shouldn't occur and be stopped from UI, but could potentially be possible from older versions
            if (profile.ScalingMode == 2 && profile.RSREnabled)
                return;

            ADLXWrapper.SetScalingMode(profile.ScalingMode);
        }
    }

    private static void SystemManager_StateChanged_IntegerScaling(bool Supported, bool Enabled)
    {
        Profile profile = ProfileManager.GetCurrent();
        if (Enabled != profile.IntegerScalingEnabled)
            ADLXWrapper.SetIntegerScaling(profile.IntegerScalingEnabled);
    }

    private static void SystemManager_StateChanged_ImageSharpening(bool Enabled, int Sharpness)
    {
        Profile profile = ProfileManager.GetCurrent();
        if (Enabled != profile.RISEnabled)
            ADLXWrapper.SetImageSharpening(profile.RISEnabled);
        if (Sharpness != profile.RISSharpness)
            ADLXWrapper.SetImageSharpeningSharpness(Sharpness);
    }

    private static void PowerProfileManager_Applied(PowerProfile profile, UpdateSource source)
    {
        // apply profile defined TDP
        if (profile.TDPOverrideEnabled && profile.TDPOverrideValues is not null)
        {
            if (!profile.AutoTDPEnabled)
            {
                // Manual TDP is set, use it and set max limit
                RequestTDP(profile.TDPOverrideValues);
                StartTDPWatchdog();
                AutoTDPMax = SettingsManager.GetInt("ConfigurableTDPOverrideUp");
            }
            else
            {
                // Both manual TDP and AutoTDP are on,
                // use manual slider as the max limit for AutoTDP
                AutoTDPMax = profile.TDPOverrideValues[0];
                StopTDPWatchdog(true);
            }
        }
        else if (cpuWatchdog.Enabled)
        {
            StopTDPWatchdog(true);

            if (!profile.AutoTDPEnabled)
            {
                // Neither manual TDP nor AutoTDP is enabled, restore default TDP
                RestoreTDP(true);
            }
            else
            {
                // AutoTDP is enabled but manual override is not, use the settings max limit
                AutoTDPMax = SettingsManager.GetInt("ConfigurableTDPOverrideUp");
            }
        }

        // apply profile defined AutoTDP
        if (profile.AutoTDPEnabled)
        {
            AutoTDPTargetFPS = profile.AutoTDPRequestedFPS;
            StartAutoTDPWatchdog();
        }
        else if (autoWatchdog.Enabled)
        {
            StopAutoTDPWatchdog(true);

            // restore default TDP (if not manual TDP is enabled)
            if (!profile.TDPOverrideEnabled)
                RestoreTDP(true);
        }

        // apply profile defined CPU
        if (profile.CPUOverrideEnabled)
        {
            RequestCPUClock(Convert.ToUInt32(profile.CPUOverrideValue));
        }
        else
        {
            // restore default GPU clock
            RestoreCPUClock(true);
        }

        // apply profile defined GPU
        if (profile.GPUOverrideEnabled)
        {
            RequestGPUClock(profile.GPUOverrideValue);
            StartGPUWatchdog();
        }
        else if (gfxWatchdog.Enabled)
        {
            // restore default GPU clock
            StopGPUWatchdog(true);
            RestoreGPUClock(true);
        }

        // apply profile defined EPP
        if (profile.EPPOverrideEnabled)
        {
            RequestEPP(profile.EPPOverrideValue);
        }
        else if (currentEPP != 0x00000032)
        {
            // restore default EPP
            RequestEPP(0x00000032);
        }

        // apply profile defined CPU Core Count
        if (profile.CPUCoreEnabled)
        {
            RequestCPUCoreCount(profile.CPUCoreCount);
        }
        else if (currentCoreCount != MotherboardInfo.NumberOfCores)
        {
            // restore default CPU Core Count
            RequestCPUCoreCount(MotherboardInfo.NumberOfCores);
        }

        // apply profile define CPU Boost
        RequestPerfBoostMode(profile.CPUBoostEnabled);

        // apply profile Power Mode
        RequestPowerMode(profile.OSPowerMode);
    }

    private static void PowerProfileManager_Discarded(PowerProfile profile)
    {
        // restore default TDP
        if (profile.TDPOverrideEnabled)
        {
            StopTDPWatchdog(true);
            RestoreTDP(true);
        }

        // restore default TDP
        if (profile.AutoTDPEnabled)
        {
            StopAutoTDPWatchdog(true);
            StopTDPWatchdog(true);
            RestoreTDP(true);
        }

        // restore default CPU frequency
        if (profile.CPUOverrideEnabled)
        {
            RestoreCPUClock(true);
        }

        // restore default GPU frequency
        if (profile.GPUOverrideEnabled)
        {
            StopGPUWatchdog(true);
            RestoreGPUClock(true);
        }

        // (un)apply profile defined EPP
        if (profile.EPPOverrideEnabled)
        {
            // restore default EPP
            RequestEPP(0x00000032);
        }

        // unapply profile defined CPU Core Count
        if (profile.CPUCoreEnabled)
        {
            RequestCPUCoreCount(MotherboardInfo.NumberOfCores);
        }

        // (un)apply profile define CPU Boost
        if (profile.CPUBoostEnabled)
        {
            RequestPerfBoostMode(false);
        }

        // restore OSPowerMode.BetterPerformance 
        RequestPowerMode(OSPowerMode.BetterPerformance);
    }

    private static void RestoreTDP(bool immediate)
    {
        for (PowerType pType = PowerType.Slow; pType <= PowerType.Fast; pType++)
            RequestTDP(pType, MainWindow.CurrentDevice.cTDP[1], immediate);
    }

    private static void RestoreCPUClock(bool immediate)
    {
        uint maxClock = MotherboardInfo.ProcessorMaxTurboSpeed;
        RequestCPUClock(maxClock);
    }

    private static void RestoreGPUClock(bool immediate)
    {
        RequestGPUClock(255 * 50, immediate);
    }

    private static void RTSS_Hooked(AppEntry appEntry)
    {
        AutoTDPProcessId = appEntry.ProcessId;
    }

    private static void RTSS_Unhooked(int processId)
    {
        AutoTDPProcessId = 0;
    }

    private static void AutoTDPWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
    {
        // We don't have any hooked process
        if (AutoTDPProcessId == 0)
            return;

        if (!autoLock)
        {
            // set lock
            autoLock = true;

            // todo: Store fps for data gathering from multiple points (OSD, Performance)
            double processValueFPS = PlatformManager.RTSS.GetFramerate(AutoTDPProcessId);

            // Ensure realistic process values, prevent divide by 0
            processValueFPS = Math.Clamp(processValueFPS, 5, 500);

            // Determine error amount, include target, actual and dipper modifier
            double controllerError = AutoTDPTargetFPS - processValueFPS - AutoTDPDipper(processValueFPS, AutoTDPTargetFPS);

            // Clamp error amount corrected within a single cycle
            // Adjust clamp if actual FPS is 2.5x requested FPS
            double clampLowerLimit = processValueFPS >= 2.5 * AutoTDPTargetFPS ? -100 : -5;
            controllerError = Math.Clamp(controllerError, clampLowerLimit, 15);

            double TDPAdjustment = controllerError * AutoTDP / processValueFPS;
            TDPAdjustment *= 0.9; // Always have a little undershoot

            // Determine final setpoint
            if (!AutoTDPFirstRun)
                AutoTDP += TDPAdjustment + AutoTDPDamper(processValueFPS);
            else
                AutoTDPFirstRun = false;

            AutoTDP = Math.Clamp(AutoTDP, TDPMin, AutoTDPMax);

            // Only update if we have a different TDP value to set
            if (AutoTDP != AutoTDPPrev)
            {
                double[] values = new double[3] { AutoTDP, AutoTDP, AutoTDP };
                RequestTDP(values, true);
            }
            AutoTDPPrev = AutoTDP;

            // LogManager.LogTrace("TDPSet;;;;;{0:0.0};{1:0.000};{2:0.0000};{3:0.0000};{4:0.0000}", AutoTDPTargetFPS, AutoTDP, TDPAdjustment, ProcessValueFPS, TDPDamping);

            // release lock
            autoLock = false;
        }
    }

    private static double AutoTDPDipper(double FPSActual, double FPSSetpoint)
    {
        // Dipper
        // Add small positive "error" if actual and target FPS are similar for a duration
        double Modifier = 0.0d;

        // Track previous FPS values for average calculation using a rolling array
        Array.Copy(FPSHistory, 0, FPSHistory, 1, FPSHistory.Length - 1);
        FPSHistory[0] = FPSActual; // Add current FPS at the start

        // Activate around target range of 1 FPS as games can fluctuate
        if (FPSSetpoint - 1 <= FPSActual && FPSActual <= FPSSetpoint + 1)
        {
            AutoTDPFPSSetpointMetCounter++;

            // First wait for three seconds of stable FPS arount target, then perform small dip
            // Reduction only happens if average FPS is on target or slightly below
            if (AutoTDPFPSSetpointMetCounter >= 3 && AutoTDPFPSSetpointMetCounter < 6 &&
                FPSSetpoint - 0.5 <= FPSHistory.Take(3).Average() && FPSHistory.Take(3).Average() <= FPSSetpoint + 0.1)
            {
                AutoTDPFPSSmallDipCounter++;
                Modifier = FPSSetpoint + 0.5 - FPSActual;
            }
            // After three small dips, perform larger dip 
            // Reduction only happens if average FPS is on target or slightly below
            else if (AutoTDPFPSSmallDipCounter >= 3 &&
                     FPSSetpoint - 0.5 <= FPSHistory.Average() && FPSHistory.Average() <= FPSSetpoint + 0.1)
            {
                Modifier = FPSSetpoint + 1.5 - FPSActual;
                AutoTDPFPSSetpointMetCounter = 6;
            }
        }
        // Perform dips until FPS is outside of limits around target
        else
        {
            Modifier = 0.0;
            AutoTDPFPSSetpointMetCounter = 0;
            AutoTDPFPSSmallDipCounter = 0;
        }

        return Modifier;
    }

    private static double AutoTDPDamper(double FPSActual)
    {
        // (PI)D derivative control component to dampen FPS fluctuations
        if (double.IsNaN(ProcessValueFPSPrevious)) ProcessValueFPSPrevious = FPSActual;
        double DFactor = -0.1d;

        // Calculation
        double deltaError = FPSActual - ProcessValueFPSPrevious;
        double DTerm = deltaError / (INTERVAL_AUTO / 1000.0);
        double TDPDamping = AutoTDP / FPSActual * DFactor * DTerm;

        ProcessValueFPSPrevious = FPSActual;

        return TDPDamping;
    }

    private static void powerWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
    {
        if (!powerLock)
        {
            // set lock
            powerLock = true;

            // Checking if active power shceme has changed to reflect that
            if (PowerGetEffectiveOverlayScheme(out var activeScheme) == 0)
                if (activeScheme != currentPowerMode)
                {
                    currentPowerMode = activeScheme;
                    var idx = Array.IndexOf(PowerModes, activeScheme);
                    if (idx != -1)
                        PowerModeChanged?.Invoke(idx);
                }

            // read perfboostmode
            var result = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFBOOSTMODE);
            var perfboostmode = result[(int)PowerIndexType.AC] == (uint)PerfBoostMode.Aggressive &&
                                result[(int)PowerIndexType.DC] == (uint)PerfBoostMode.Aggressive;

            if (perfboostmode != currentPerfBoostMode)
            {
                currentPerfBoostMode = perfboostmode;
                PerfBoostModeChanged?.Invoke(perfboostmode);
            }

            // Checking if current EPP value has changed to reflect that
            var EPP = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFEPP);
            var DCvalue = EPP[(int)PowerIndexType.DC];

            if (DCvalue != currentEPP)
            {
                currentEPP = DCvalue;
                EPPChanged?.Invoke(DCvalue);
            }

            // release lock
            powerLock = false;
        }
    }

    private static async void cpuWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
    {
        if (processor is null || !processor.IsInitialized)
            return;

        if (!cpuLock)
        {
            // set lock
            cpuLock = true;

            var TDPdone = false;
            var MSRdone = false;

            // read current values and (re)apply requested TDP if needed
            foreach (PowerType type in (PowerType[])Enum.GetValues(typeof(PowerType)))
            {
                var idx = (int)type;

                // skip msr
                if (idx >= StoredTDP.Length)
                    break;

                var TDP = StoredTDP[idx];

                if (processor is AMDProcessor)
                {
                    // AMD reduces TDP by 10% when OS power mode is set to Best power efficiency
                    if (currentPowerMode == OSPowerMode.BetterBattery)
                        TDP = (int)Math.Truncate(TDP * 0.9);
                }
                else if (processor is IntelProcessor)
                {
                    // Intel doesn't have stapm
                    if (type == PowerType.Stapm)
                        continue;
                }

                var ReadTDP = CurrentTDP[idx];

                if (ReadTDP != 0)
                    cpuWatchdog.Interval = INTERVAL_DEFAULT;
                else
                    cpuWatchdog.Interval = INTERVAL_DEGRADED;

                // only request an update if current limit is different than stored
                if (ReadTDP != TDP)
                    processor.SetTDPLimit(type, TDP);

                await Task.Delay(12);
            }

            // are we done ?
            TDPdone = CurrentTDP[0] == StoredTDP[0] && CurrentTDP[1] == StoredTDP[1] && CurrentTDP[2] == StoredTDP[2];

            // processor specific
            if (processor is IntelProcessor)
            {
                int TDPslow = (int)StoredTDP[(int)PowerType.Slow];
                int TDPfast = (int)StoredTDP[(int)PowerType.Fast];

                // only request an update if current limit is different than stored
                if (CurrentTDP[(int)PowerType.MsrSlow] != TDPslow ||
                    CurrentTDP[(int)PowerType.MsrFast] != TDPfast)
                    ((IntelProcessor)processor).SetMSRLimit(TDPslow, TDPfast);
                else
                    MSRdone = true;
            }

            // user requested to halt cpu watchdog
            if (cpuWatchdogPendingStop)
            {
                if (cpuWatchdog.Interval == INTERVAL_DEFAULT)
                {
                    if (TDPdone && MSRdone)
                        cpuWatchdog.Stop();
                }
                else if (cpuWatchdog.Interval == INTERVAL_DEGRADED)
                {
                    cpuWatchdog.Stop();
                }
            }

            // release lock
            cpuLock = false;
        }
    }

    private static void gfxWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
    {
        if (processor is null || !processor.IsInitialized)
            return;

        if (!gfxLock)
        {
            // set lock
            gfxLock = true;

            var GPUdone = false;

            if (CurrentGfxClock != 0)
                gfxWatchdog.Interval = INTERVAL_DEFAULT;
            else
                gfxWatchdog.Interval = INTERVAL_DEGRADED;

            // not ready yet
            if (StoredGfxClock == 0)
            {
                // release lock
                gfxLock = false;
                return;
            }

            // only request an update if current gfx clock is different than stored
            if (CurrentGfxClock != StoredGfxClock)
            {
                // disabling
                if (StoredGfxClock == 12750)
                    GPUdone = true;
                else
                    processor.SetGPUClock(StoredGfxClock);
            }
            else
            {
                GPUdone = true;
            }

            // user requested to halt gpu watchdog
            if (gfxWatchdogPendingStop)
            {
                if (gfxWatchdog.Interval == INTERVAL_DEFAULT)
                {
                    if (GPUdone)
                        gfxWatchdog.Stop();
                }
                else if (gfxWatchdog.Interval == INTERVAL_DEGRADED)
                {
                    gfxWatchdog.Stop();
                }
            }

            // release lock
            gfxLock = false;
        }
    }

    private static void StartGPUWatchdog()
    {
        gfxWatchdogPendingStop = false;
        gfxWatchdog.Interval = INTERVAL_DEFAULT;
        gfxWatchdog.Start();
    }

    private static void StopGPUWatchdog(bool immediate = false)
    {
        gfxWatchdogPendingStop = true;
        if (immediate)
            gfxWatchdog.Stop();
    }

    private static void StartTDPWatchdog()
    {
        cpuWatchdogPendingStop = false;
        cpuWatchdog.Interval = INTERVAL_DEFAULT;
        cpuWatchdog.Start();
    }

    private static void StopTDPWatchdog(bool immediate = false)
    {
        cpuWatchdogPendingStop = true;
        if (immediate)
            cpuWatchdog.Stop();
    }

    private static void StartAutoTDPWatchdog()
    {
        autoWatchdog.Start();
    }

    private static void StopAutoTDPWatchdog(bool immediate = false)
    {
        autoWatchdog.Stop();
    }

    private static void RequestTDP(PowerType type, double value, bool immediate = false)
    {
        if (processor is null || !processor.IsInitialized)
            return;

        // make sure we're not trying to run below or above specs
        value = Math.Min(TDPMax, Math.Max(TDPMin, value));

        // update value read by timer
        int idx = (int)type;
        StoredTDP[idx] = value;

        // immediately apply
        if (immediate)
            processor.SetTDPLimit((PowerType)idx, value, immediate);
    }

    private static async void RequestTDP(double[] values, bool immediate = false)
    {
        if (processor is null || !processor.IsInitialized)
            return;

        for (int idx = (int)PowerType.Slow; idx <= (int)PowerType.Fast; idx++)
        {
            // make sure we're not trying to run below or above specs
            values[idx] = Math.Min(TDPMax, Math.Max(TDPMin, values[idx]));

            // update value read by timer
            StoredTDP[idx] = values[idx];

            // immediately apply
            if (immediate)
            {
                processor.SetTDPLimit((PowerType)idx, values[idx], immediate);
                await Task.Delay(12);
            }
        }
    }

    private static void RequestGPUClock(double value, bool immediate = false)
    {
        if (processor is null || !processor.IsInitialized)
            return;

        // update value read by timer
        StoredGfxClock = value;

        // immediately apply
        if (immediate)
            processor.SetGPUClock(value);
    }

    private static void RequestPowerMode(Guid guid)
    {
        currentPowerMode = guid;
        LogManager.LogDebug("User requested power scheme: {0}", currentPowerMode);

        if (PowerSetActiveOverlayScheme(currentPowerMode) != 0)
            LogManager.LogWarning("Failed to set requested power scheme: {0}", currentPowerMode);
    }

    private static void RequestEPP(uint EPPOverrideValue)
    {
        currentEPP = EPPOverrideValue;

        var requestedEPP = new uint[2]
        {
            (uint)Math.Max(0, (int)EPPOverrideValue - 17),
            (uint)Math.Max(0, (int)EPPOverrideValue)
        };

        // Is the EPP value already correct?
        uint[] EPP = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFEPP);
        if (EPP[0] == requestedEPP[0] && EPP[1] == requestedEPP[1])
            return;

        LogManager.LogDebug("User requested EPP AC: {0}, DC: {1}", requestedEPP[0], requestedEPP[1]);

        // Set profile EPP
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFEPP, requestedEPP[0], requestedEPP[1]);
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFEPP1, requestedEPP[0], requestedEPP[1]);

        // Has the EPP value been applied?
        EPP = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFEPP);
        if (EPP[0] != requestedEPP[0] || EPP[1] != requestedEPP[1])
            LogManager.LogWarning("Failed to set requested EPP");
    }

    private static void RequestCPUCoreCount(int CoreCount)
    {
        currentCoreCount = CoreCount;

        uint currentCoreCountPercent = (uint)((100.0d / MotherboardInfo.NumberOfCores) * CoreCount);

        // Is the CPMINCORES value already correct?
        uint[] CPMINCORES = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMINCORES);
        bool CPMINCORESReady = (CPMINCORES[0] == currentCoreCountPercent && CPMINCORES[1] == currentCoreCountPercent);

        // Is the CPMAXCORES value already correct?
        uint[] CPMAXCORES = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMAXCORES);
        bool CPMAXCORESReady = (CPMAXCORES[0] == currentCoreCountPercent && CPMAXCORES[1] == currentCoreCountPercent);

        if (CPMINCORESReady && CPMAXCORESReady)
            return;

        // Set profile CPMINCORES and CPMAXCORES
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMINCORES, currentCoreCountPercent, currentCoreCountPercent);
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMAXCORES, currentCoreCountPercent, currentCoreCountPercent);

        LogManager.LogDebug("User requested CoreCount: {0} ({1}%)", CoreCount, currentCoreCountPercent);

        // Has the CPMINCORES value been applied?
        CPMINCORES = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMINCORES);
        if (CPMINCORES[0] != currentCoreCountPercent || CPMINCORES[1] != currentCoreCountPercent)
            LogManager.LogWarning("Failed to set requested CPMINCORES");

        // Has the CPMAXCORES value been applied?
        CPMAXCORES = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMAXCORES);
        if (CPMAXCORES[0] != currentCoreCountPercent || CPMAXCORES[1] != currentCoreCountPercent)
            LogManager.LogWarning("Failed to set requested CPMAXCORES");
    }

    private static void RequestPerfBoostMode(bool value)
    {
        currentPerfBoostMode = value;

        var perfboostmode = value ? (uint)PerfBoostMode.Aggressive : (uint)PerfBoostMode.Enabled;
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFBOOSTMODE, perfboostmode, perfboostmode);

        LogManager.LogDebug("User requested perfboostmode: {0}", value);
    }

    private static void RequestCPUClock(uint cpuClock)
    {
        double maxClock = MotherboardInfo.ProcessorMaxTurboSpeed;

        // Is the PROCFREQMAX value already correct?
        uint[] currentClock = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PROCFREQMAX);
        bool IsReady = (currentClock[0] == cpuClock && currentClock[1] == cpuClock);

        if (IsReady)
            return;

        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PROCFREQMAX, cpuClock, cpuClock);

        double cpuPercentage = cpuClock / maxClock * 100.0d;
        LogManager.LogDebug("User requested PROCFREQMAX: {0} ({1}%)", cpuClock, cpuPercentage);

        // Has the value been applied?
        currentClock = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PROCFREQMAX);
        if (currentClock[0] != cpuClock || currentClock[1] != cpuClock)
            LogManager.LogWarning("Failed to set requested PROCFREQMAX");
    }

    public static void Start()
    {
        // initialize watchdog(s)
        powerWatchdog.Start();

        // initialize processor
        processor = Processor.GetCurrent();

        if (processor.IsInitialized)
        {
            processor.StatusChanged += Processor_StatusChanged;
            processor.Initialize();
        }

        // deprecated
        /*
        processor.ValueChanged += Processor_ValueChanged;
        processor.LimitChanged += Processor_LimitChanged;
        processor.MiscChanged += Processor_MiscChanged;
        */

        IsInitialized = true;
        Initialized?.Invoke();

        LogManager.LogInformation("{0} has started", "PerformanceManager");
    }

    public static void Stop()
    {
        if (!IsInitialized)
            return;

        if (processor.IsInitialized)
            processor.Stop();

        powerWatchdog.Stop();
        cpuWatchdog.Stop();
        gfxWatchdog.Stop();
        autoWatchdog.Stop();

        IsInitialized = false;

        LogManager.LogInformation("{0} has started", "PerformanceManager");
    }

    public static Processor GetProcessor()
    {
        return processor;
    }

    #region imports

    /// <summary>
    ///     Retrieves the active overlay power scheme and returns a GUID that identifies the scheme.
    /// </summary>
    /// <param name="EffectiveOverlayPolicyGuid">A pointer to a GUID structure.</param>
    /// <returns>Returns zero if the call was successful, and a nonzero value if the call failed.</returns>
    [DllImportAttribute("powrprof.dll", EntryPoint = "PowerGetEffectiveOverlayScheme")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid EffectiveOverlayPolicyGuid);

    /// <summary>
    ///     Sets the active power overlay power scheme.
    /// </summary>
    /// <param name="OverlaySchemeGuid">The identifier of the overlay power scheme.</param>
    /// <returns>Returns zero if the call was successful, and a nonzero value if the call failed.</returns>
    [DllImportAttribute("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
    private static extern uint PowerSetActiveOverlayScheme(Guid OverlaySchemeGuid);

    #endregion

    #region events

    public static event LimitChangedHandler PowerLimitChanged;

    public delegate void LimitChangedHandler(PowerType type, int limit);

    public static event ValueChangedHandler PowerValueChanged;

    public delegate void ValueChangedHandler(PowerType type, float value);

    public static event StatusChangedHandler ProcessorStatusChanged;

    public delegate void StatusChangedHandler(bool CanChangeTDP, bool CanChangeGPU);

    public static event PowerModeChangedEventHandler PowerModeChanged;

    public delegate void PowerModeChangedEventHandler(int idx);

    public static event PerfBoostModeChangedEventHandler PerfBoostModeChanged;

    public delegate void PerfBoostModeChangedEventHandler(bool value);

    public static event EPPChangedEventHandler EPPChanged;

    public delegate void EPPChangedEventHandler(uint EPP);

    #endregion

    #region events

    private static void LibreHardwareMonitor_GPUClockChanged(float? value)
    {
        if (value is null) return;

        CurrentGfxClock = (float) value;

        LogManager.LogTrace("GPUClockChange: {0} Mhz", value);
    }

    private static void Processor_StatusChanged(bool CanChangeTDP, bool CanChangeGPU)
    {
        ProcessorStatusChanged?.Invoke(CanChangeTDP, CanChangeGPU);
    }

    [Obsolete("Method is deprecated.")]
    private static void Processor_ValueChanged(PowerType type, float value)
    {
        PowerValueChanged?.Invoke(type, value);
    }

    [Obsolete("Method is deprecated.")]
    private static void Processor_LimitChanged(PowerType type, int limit)
    {
        var idx = (int)type;
        CurrentTDP[idx] = limit;

        // raise event
        PowerLimitChanged?.Invoke(type, limit);
    }

    [Obsolete("Method is deprecated.")]
    private static void Processor_MiscChanged(string misc, float value)
    {
        switch (misc)
        {
            case "gfx_clk":
                {
                    CurrentGfxClock = value;
                }
                break;
        }
    }

    #endregion
}
