using System;
using Newtonsoft.Json.Linq;
using ReactorV.Integration;

namespace ReactorV.Extension.Examples
{
    /// <summary>
    /// A compiled reference for connecting an ALLIN1-style gameplay service
    /// to Reactor without giving the browser direct game or save-file access.
    /// Construct and dispose this adapter from the owning GTA script.
    /// </summary>
    public sealed class AllIn1ExtensionExample : IDisposable, IReactorExtensionLifecycle
    {
        private readonly IAllIn1FeatureService _service;
        private readonly IReactorExtensionHandle _handle;

        public AllIn1ExtensionExample(IAllIn1FeatureService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _handle = ReactorApi.RegisterExtension(
                new ReactorExtensionDescriptor(
                    "allin1.online",
                    "ALLIN1 Online Content",
                    "1.0.0",
                    "GBAY, garages, traffic, and save-aware settings.",
                    new[] { "gbay", "garages", "traffic", "save-participant" }),
                Configure);
        }

        public void Dispose() => _handle.Dispose();

        public bool PublishOrderChanged(string receiptId, string state) =>
            _handle.TryPublishEvent(
                "gbay.orderchanged",
                new JObject { ["receiptId"] = receiptId, ["state"] = state });

        public void OnLifecycle(ReactorLifecycleContext context)
        {
            if (context.Stage == ReactorLifecycleStage.StoryReady ||
                context.Stage == ReactorLifecycleStage.Resumed)
            {
                RefreshMainMenu();
            }
        }

        private void Configure(ReactorExtensionBuilder builder)
        {
            builder.AddAction(
                new ReactorActionDescriptor(
                    "gbay.purchase",
                    "Purchase vehicle",
                    ReactorActionRisk.Persistent,
                    new[]
                    {
                        new ReactorParameterDescriptor(
                            "listingId",
                            ReactorValueType.String,
                            required: true,
                            maximumLength: 96),
                    },
                    description: "Stages a GBAY purchase for the next normal GTA save."),
                (_, parameters) =>
                {
                    var receipt = _service.PurchaseVehicle(parameters.Value<string>("listingId")!);
                    return ReactorActionResult.Success(new JObject
                    {
                        ["receiptId"] = receipt,
                        ["savePending"] = true,
                    });
                });

            builder.AddAction(
                new ReactorActionDescriptor(
                    "traffic.setenabled",
                    "Spawn add-on vehicles in traffic",
                    ReactorActionRisk.Persistent,
                    new[]
                    {
                        new ReactorParameterDescriptor("value", ReactorValueType.Boolean, required: true),
                    }),
                (_, parameters) => ReactorActionResult.Success(new JObject
                {
                    ["enabled"] = _service.SetTrafficEnabled(parameters.Value<bool>("value")),
                    ["savePending"] = true,
                }));

            builder.AddAction(
                new ReactorActionDescriptor(
                    "garage.deliver",
                    "Deliver selected vehicle",
                    ReactorActionRisk.Gameplay,
                    new[]
                    {
                        new ReactorParameterDescriptor("vehicleId", ReactorValueType.String, required: true),
                        new ReactorParameterDescriptor("garageId", ReactorValueType.String, required: true),
                    },
                    requiresConfirmation: true),
                (_, parameters) => ReactorActionResult.Success(new JObject
                {
                    ["delivered"] = _service.DeliverVehicle(
                        parameters.Value<string>("vehicleId")!,
                        parameters.Value<string>("garageId")!),
                }));

            builder.AddAction(
                new ReactorActionDescriptor(
                    "gbay.search",
                    "Search listings",
                    ReactorActionRisk.Read,
                    new[]
                    {
                        new ReactorParameterDescriptor("value", ReactorValueType.String, required: true),
                    }),
                (_, parameters) => ReactorActionResult.Success(new JObject
                {
                    ["query"] = parameters.Value<string>("value"),
                }));

            builder.AddEvent(new ReactorEventDescriptor("gbay.orderchanged"));
            builder.AddEvent(new ReactorEventDescriptor("save.statechanged"));
            builder.AddMenu(CreateMainMenu());
            builder.AddMenu(new ReactorMenuDescriptor(
                "gbay",
                "Purchase Vehicles",
                new ReactorMenuNode[]
                {
                    new ReactorSearchNode(
                        "search",
                        "Search",
                        "gbay.search",
                        placeholder: "Vehicle name",
                        description: "A real storefront would bind search to its own read action."),
                    new ReactorStatusNode(
                        "purchase-help",
                        "Purchase",
                        "Pass the selected listingId when activating the purchase action."),
                }));
            builder.UseLifecycle(this);
        }

        private ReactorMenuDescriptor CreateMainMenu() => new ReactorMenuDescriptor(
            "main",
            "ALLIN1",
            new ReactorMenuNode[]
            {
                new ReactorSubmenuNode("purchase-weapons", "Purchase Vehicles", "gbay"),
                new ReactorToggleNode(
                    "traffic",
                    "Add-on vehicles in traffic",
                    "traffic.setenabled",
                    _service.TrafficEnabled),
                new ReactorStatusNode(
                    "save-state",
                    "Save status",
                    _service.SavePending ? "Changes pending GTA save" : "Saved",
                    _service.SavePending ? "warning" : "success"),
            },
            "Example main menu backed by authoritative gameplay services.");

        private void RefreshMainMenu() => _handle.UpdateMenu(CreateMainMenu());
    }

    public interface IAllIn1FeatureService
    {
        bool TrafficEnabled { get; }
        bool SavePending { get; }
        string PurchaseVehicle(string listingId);
        bool SetTrafficEnabled(bool enabled);
        bool DeliverVehicle(string vehicleId, string garageId);
    }
}
