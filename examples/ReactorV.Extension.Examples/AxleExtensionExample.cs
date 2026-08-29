using System;
using Newtonsoft.Json.Linq;
using ReactorV.Integration;

namespace ReactorV.Extension.Examples
{
    /// <summary>High-rate read telemetry plus a typed per-vehicle control.</summary>
    public sealed class AxleExtensionExample : IDisposable
    {
        private readonly IAxleFeatureService _service;
        private readonly IReactorExtensionHandle _handle;

        public AxleExtensionExample(IAxleFeatureService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _handle = ReactorApi.RegisterExtension(
                new ReactorExtensionDescriptor(
                    "allin1.axles",
                    "ALLIN1 Axle Runtime",
                    "1.0.0",
                    capabilities: new[] { "vehicle.axles", "telemetry" }),
                builder =>
                {
                    builder.AddAction(
                        new ReactorActionDescriptor(
                            "steering.setgain",
                            "Set steering gain",
                            ReactorActionRisk.Gameplay,
                            new[]
                            {
                                new ReactorParameterDescriptor(
                                    "value",
                                    ReactorValueType.Number,
                                    required: true,
                                    minimum: -2,
                                    maximum: 2),
                            }),
                        (_, parameters) => ReactorActionResult.Success(new JObject
                        {
                            ["gain"] = _service.SetActiveSteeringGain(parameters.Value<double>("value")),
                        }));
                    builder.AddEvent(new ReactorEventDescriptor("telemetry", maximumPayloadBytes: 4096));
                    builder.AddMenu(new ReactorMenuDescriptor(
                        "axles",
                        "Axle Runtime",
                        new ReactorMenuNode[]
                        {
                            new ReactorRangeNode(
                                "steering-gain",
                                "Steering gain",
                                "steering.setgain",
                                service.ActiveSteeringGain,
                                -2,
                                2,
                                0.05),
                            new ReactorStatusNode("vehicle", "Active vehicle", service.ActiveVehicleModel),
                        }));
                });
        }

        public void PublishTelemetry() => _handle.TryPublishEvent(
            "telemetry",
            new JObject
            {
                ["vehicle"] = _service.ActiveVehicleModel,
                ["wheelCount"] = _service.WheelCount,
                ["speedMps"] = _service.SpeedMetersPerSecond,
                ["steeringGain"] = _service.ActiveSteeringGain,
            });

        public void Dispose() => _handle.Dispose();
    }

    public interface IAxleFeatureService
    {
        string ActiveVehicleModel { get; }
        int WheelCount { get; }
        double SpeedMetersPerSecond { get; }
        double ActiveSteeringGain { get; }
        double SetActiveSteeringGain(double gain);
    }
}
