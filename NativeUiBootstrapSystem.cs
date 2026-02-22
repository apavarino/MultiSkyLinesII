using Game;
using Game.UI;

namespace MultiSkyLineII
{
    public sealed class NativeUiBootstrapSystem : UISystemBase
    {
        public override GameMode gameMode => GameMode.GameOrEditor;

        protected override void OnCreate()
        {
            base.OnCreate();
            ModDiagnostics.Write("NativeUiBootstrapSystem.OnCreate");
        }

        protected override void OnUpdate()
        {
        }
    }
}
