using System;
using Loupedeck.ClaudeCodePlugin.Services;

namespace Loupedeck.ClaudeCodePlugin;

// AgentPad for IDEs. The internal name ClaudeCodeSessions is what saved profiles use to find its
// keys. Not tied to an application: its keys work from any profile.
public class ClaudeCodeSessionsPlugin : Plugin {
  public override bool UsesApplicationApiOnly => true;
  public override bool HasNoApplication => true;

  public ClaudeCodeSessionsPlugin() => PluginLog.Init(Log);

  public override void Load() {
    PluginLog.Info("AgentPad for IDEs loading");
    SetupService.Run();
    _ = ClaudeSessionService.Instance;
    _ = PulseClock.Instance;
  }

  public override void Unload() {
    PluginLog.Info("AgentPad for IDEs unloading");
    PulseClock.Instance.Dispose();
    ClaudeSessionService.Instance.Dispose();
  }
}

// The plugin service expects an application class even for plugins without an application
public class ClaudeCodeSessionsApplication : ClientApplication {
  protected override string GetProcessName() => "";

  protected override string GetBundleName() => "";

  public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
}
