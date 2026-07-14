using System;
using System.Linq;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;

namespace HrothgarMakeCoin
{
  /// <summary>
  /// Bridges HrothgarMakeCoin with AutoRetainer.
  ///
  /// Two integrations:
  ///   1. The legacy "Suppressed" IPC (via EzIPC) used to pause AutoRetainer's multi-mode while a
  ///      manual Auto Pinch runs.
  ///   2. The retainer post-processing hook: after AutoRetainer finishes a retainer's ventures it
  ///      fires a "step" event; we request post-processing, and when AutoRetainer hands us the
  ///      retainer (its action menu open) we re-price that retainer's listings, then release it.
  ///
  /// The post-processing channels are subscribed to directly (mirroring PunishXIV/AutoRetainerAPI)
  /// so we don't need to add the AutoRetainerAPI project — which pulls a second, conflicting copy
  /// of ECommons via NuGet — on top of this repo's ECommons submodule.
  /// </summary>
  public class AutoRetainerIPC
  {
    public const string Name = "AutoRetainer";
    public static bool Installed => Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == Name && x.IsLoaded);

    // IPC channel names, verbatim from AutoRetainerAPI/ApiConsts.cs.
    private const string ChannelRetainerAdditionalTask = "AutoRetainer.OnRetainerAdditionalTask";
    private const string ChannelRetainerReadyForPostprocess = "AutoRetainer.OnRetainerReadyForPostprocess";
    private const string ChannelRequestPostprocess = "AutoRetainer.RequestPostprocess";
    private const string ChannelFinishPostprocess = "AutoRetainer.FinishPostprocessRequest";

    private static AutoRetainerIPC? _instance;
    public static AutoRetainerIPC? Instance => _instance;

    #nullable disable
    [EzIPC] public readonly Func<bool> GetSuppressed;
    [EzIPC] public readonly Action<bool> SetSuppressed;
    #nullable enable

    private readonly AutoPinch _autoPinch;
    private readonly string _pluginName;
    private readonly Action<string> _onStep;
    private readonly Action<string, string> _onReady;

    private AutoRetainerIPC(AutoPinch autoPinch)
    {
      _autoPinch = autoPinch;
      _pluginName = Svc.PluginInterface.InternalName;

      // Wires GetSuppressed / SetSuppressed.
      EzIPC.Init(this, Name);

      // Post-processing hook.
      _onStep = OnRetainerPostprocessStep;
      _onReady = OnRetainerReadyForPostprocess;
      Svc.PluginInterface.GetIpcSubscriber<string, object>(ChannelRetainerAdditionalTask).Subscribe(_onStep);
      Svc.PluginInterface.GetIpcSubscriber<string, string, object>(ChannelRetainerReadyForPostprocess).Subscribe(_onReady);
    }

    public static bool Suppressed() => _instance != null && _instance.GetSuppressed();
    public static bool Suppressed(bool value)
    {
      Svc.Log.Debug($"AR Suppressed={value}");
      _instance?.SetSuppressed(value);
      return true;
    }

    internal static void Initialize(AutoPinch autoPinch)
    {
      if (Installed)
        _instance = new AutoRetainerIPC(autoPinch);
    }

    public static void Dispose()
    {
      _instance?.Unhook();
      _instance = null;
    }

    private void Unhook()
    {
      try
      {
        Svc.PluginInterface.GetIpcSubscriber<string, object>(ChannelRetainerAdditionalTask).Unsubscribe(_onStep);
        Svc.PluginInterface.GetIpcSubscriber<string, string, object>(ChannelRetainerReadyForPostprocess).Unsubscribe(_onReady);
      }
      catch (Exception ex)
      {
        Svc.Log.Warning(ex, "Failed to unsubscribe from AutoRetainer post-process IPC");
      }
    }

    private void RequestRetainerPostprocess()
    {
      try
      {
        Svc.PluginInterface.GetIpcSubscriber<string, object>(ChannelRequestPostprocess).InvokeAction(_pluginName);
      }
      catch (Exception ex)
      {
        Svc.Log.Warning(ex, "Failed to request AutoRetainer retainer post-process");
      }
    }

    private void FinishRetainerPostProcess()
    {
      try
      {
        Svc.PluginInterface.GetIpcSubscriber<object>(ChannelFinishPostprocess).InvokeAction();
      }
      catch (Exception ex)
      {
        Svc.Log.Warning(ex, "Failed to finish AutoRetainer retainer post-process");
      }
    }

    /// <summary>
    /// STEP event (fired for every subscriber). This is the request window: if we want to act on
    /// this retainer we MUST call RequestRetainerPostprocess synchronously here so AutoRetainer
    /// includes us in the following post-process pass.
    /// </summary>
    private void OnRetainerPostprocessStep(string retainerName)
    {
      try
      {
        if (!Plugin.Configuration.AutoPinchAfterAutoRetainer)
          return;

        if (Plugin.Configuration.AutoRetainerRespectRetainerSelection && !IsRetainerEnabled(retainerName))
        {
          Svc.Log.Debug($"AutoRetainer post-venture pinch skipping '{retainerName}' (not in retainer selection)");
          return;
        }

        Svc.Log.Debug($"AutoRetainer post-venture pinch requested for '{retainerName}'");
        RequestRetainerPostprocess();
      }
      catch (Exception ex)
      {
        Svc.Log.Error(ex, "Error handling AutoRetainer post-process step");
      }
    }

    /// <summary>
    /// READY event (broadcast with the target plugin name; we filter to ourselves). Our turn:
    /// the retainer is selected with its action menu open, and AutoRetainer is blocked until we
    /// call FinishRetainerPostProcess. Drive the re-pricing, then release AutoRetainer.
    /// </summary>
    private void OnRetainerReadyForPostprocess(string plugin, string retainerName)
    {
      if (plugin != _pluginName)
        return;

      try
      {
        _autoPinch.StartAutoRetainerPinch(retainerName, FinishRetainerPostProcess);
      }
      catch (Exception ex)
      {
        Svc.Log.Error(ex, "Error starting AutoRetainer post-venture pinch");
        FinishRetainerPostProcess(); // never leave AutoRetainer hanging
      }
    }

    private static bool IsRetainerEnabled(string retainerName)
    {
      var enabled = Plugin.Configuration.EnabledRetainerNames;
      if (enabled.Contains(Configuration.ALL_DISABLED_SENTINEL))
        return false;

      // Empty set means "all enabled"; otherwise it's an explicit whitelist.
      return enabled.Count == 0 || enabled.Contains(retainerName);
    }
  }
}
