using System;
using Newtonsoft.Json.Linq;
using ReactorV.Integration;

namespace ReactorV.Starter
{
    // No ALLIN1, game natives, files, timers, F9 ownership, or automatic menu opening.
    public sealed class StarterExtension : IDisposable
    {
        private readonly IReactorExtensionHandle _handle;
        private readonly string _title;
        public bool Enabled { get; private set; } = true;
        public double Strength { get; private set; } = 50;
        public int SelectedItem { get; private set; }

        public StarterExtension(string id, string title)
        {
            _title = title;
            _handle = ReactorApi.RegisterExtension(new ReactorExtensionDescriptor(id, title, "0.1.0",
                "Independent Reactor starter: settings, scrolling list, grid, confirmation, and status."), builder =>
            {
                var toggle = MenuPrefabs.Toggle(builder, "enabled", "Example enabled", Enabled,
                    value => { Enabled = value; Refresh(); });
                var range = MenuPrefabs.Range(builder, "strength", "Example strength", Strength, 0, 100, 5,
                    value => { Strength = value; Refresh(); });
                var reset = MenuPrefabs.ConfirmedAction(builder, "reset", "Reset example settings",
                    "Only this starter's in-memory settings will be reset. Other mods are unchanged.",
                    (_, __) => { Enabled = true; Strength = 50; SelectedItem = 0; Refresh(); return ReactorActionResult.Success(); });
                builder.AddAction(new ReactorActionDescriptor("select", "Select example item", ReactorActionRisk.Gameplay,
                    new[] { new ReactorParameterDescriptor("item", ReactorValueType.Integer,
                        required: true, minimum: 1, maximum: 32) }),
                    (_, parameters) => { SelectedItem = parameters.Value<int>("item"); Refresh(); return ReactorActionResult.Success(); });
                builder.AddMenu(MainMenu());
                builder.AddMenu(MenuPrefabs.Settings("settings", "Settings", new ReactorMenuNode[] { toggle, range, reset }));
                builder.AddMenu(MenuPrefabs.ScrollList("list", "Scrolling list", MenuPrefabs.BoundRows("select", 32)));
                builder.AddMenu(MenuPrefabs.CardGrid("grid", "Card grid", MenuPrefabs.BoundRows("select", 8)));
                builder.AddMenu(StatusMenu());
            });
        }

        public bool ToggleMenu()
        {
            var presentation = _handle as IReactorMenuPresentationHandle;
            if (presentation == null) return false;
            // The runtime owns visibility/ACKs. Do not cache a separate local open flag.
            return presentation.IsMenuPresented("main")
                ? presentation.TryDismissMenu("main")
                : presentation.TryPresentMenu("main");
        }

        private ReactorMenuDescriptor MainMenu() => new ReactorMenuDescriptor("main", _title, new ReactorMenuNode[]
        {
            new ReactorStatusNode("independent", "Runtime", "Shared Reactor; no ALLIN1 required", "success"),
            new ReactorSubmenuNode("settings", "Settings", "settings"),
            new ReactorSubmenuNode("list", "Scrolling list", "list"),
            new ReactorSubmenuNode("grid", "Card grid", "grid"),
            new ReactorSubmenuNode("status", "Status panel", "status"),
        });

        private ReactorMenuDescriptor StatusMenu() => MenuPrefabs.StatusPanel("status", _title + " status", new[]
        {
            new ReactorStatusNode("enabled", "Example enabled", Enabled ? "On" : "Off"),
            new ReactorStatusNode("strength", "Example strength", Strength.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new ReactorStatusNode("selected", "Selected item", SelectedItem == 0 ? "None" : SelectedItem.ToString()),
        });

        private void Refresh()
        {
            _handle.UpdateMenu(MenuPrefabs.Settings("settings", "Settings", new ReactorMenuNode[]
            {
                new ReactorToggleNode("enabled", "Example enabled", "enabled", Enabled),
                new ReactorRangeNode("strength", "Example strength", "strength", Strength, 0, 100, 5),
                new ReactorActionNode("reset", "Reset example settings", "reset",
                    "Only this starter's in-memory settings will be reset. Other mods are unchanged."),
            }));
            _handle.UpdateMenu(StatusMenu());
        }

        public void Dispose() => _handle.Dispose();
    }
}
