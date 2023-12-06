using Menu.Remix.MixedUI;
using UnityEngine;

namespace SlugTemplate
{
    internal class RemixMenu : OptionInterface
    {
        public readonly Configurable<bool> SlupsSpawnSlups;
        public readonly Configurable<bool> ElectricHint;

        public RemixMenu(Plugin modInstance)
        {
            SlupsSpawnSlups = config.Bind<bool>("SlupsSpawnSlups", false, new ConfigurableInfo("Whether or not slugpups are prevented from spawning more slugpups. Enabling this will most likely crash your game."));
            ElectricHint = config.Bind<bool>("ElectricHint", true, new ConfigurableInfo("Whether or not electric death is always present, in a minor form during the cycle, by default."));
        }

        public override void Initialize()
        {
            base.Initialize();

            // Initialize tab
            var opTab = new OpTab(this, "Options");
            this.Tabs = new[]
            {
                opTab
            };

            // Add stuff to tab
            opTab.AddItems(
                new OpLabel(10f, 560f, "OPTIONS", true),
                new OpCheckBox(SlupsSpawnSlups, new(10f, 530f)),
                new OpLabel(40f, 530f, "Slugpups spawn more slugpups going through pipes"),
                new OpCheckBox(ElectricHint, new(10f, 500f)),
                new OpLabel(40f, 500f, "Electric death always present")
            );
        }
    }
}
