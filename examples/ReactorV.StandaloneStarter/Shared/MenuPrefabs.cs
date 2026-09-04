using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using ReactorV.Integration;

namespace ReactorV.Starter
{
    // Source prefabs: compiled into the consumer, not another shared runtime DLL.
    public static class MenuPrefabs
    {
        public static ReactorMenuDescriptor Settings(string id, string title,
            IEnumerable<ReactorMenuNode> controls) => new ReactorMenuDescriptor(id, title, controls);

        public static ReactorMenuDescriptor ScrollList(string id, string title,
            IEnumerable<ReactorMenuNode> rows) => new ReactorMenuDescriptor(id, title,
                new[] { new ReactorListNode("items", title, rows) });

        public static ReactorMenuDescriptor CardGrid(string id, string title,
            IEnumerable<ReactorMenuNode> cards, int columns = 2) => new ReactorMenuDescriptor(id, title,
                new[] { new ReactorGridNode("items", title, cards, columns) });

        public static ReactorMenuDescriptor StatusPanel(string id, string title,
            IEnumerable<ReactorStatusNode> statuses) => new ReactorMenuDescriptor(id, title, statuses);

        public static ReactorToggleNode Toggle(ReactorExtensionBuilder builder, string id,
            string label, bool value, Action<bool> apply)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            builder.AddAction(new ReactorActionDescriptor(id, label, ReactorActionRisk.Gameplay,
                new[] { new ReactorParameterDescriptor("value", ReactorValueType.Boolean, required: true) }),
                (_, parameters) => { apply(parameters.Value<bool>("value")); return ReactorActionResult.Success(); });
            return new ReactorToggleNode(id, label, id, value);
        }

        public static ReactorRangeNode Range(ReactorExtensionBuilder builder, string id,
            string label, double value, double minimum, double maximum, double step, Action<double> apply)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            // Construct the node before registering its action so invalid limits cannot leave a partial prefab.
            var node = new ReactorRangeNode(id, label, id, value, minimum, maximum, step);
            builder.AddAction(new ReactorActionDescriptor(id, label, ReactorActionRisk.Gameplay,
                new[] { new ReactorParameterDescriptor("value", ReactorValueType.Number,
                    required: true, minimum: minimum, maximum: maximum) }),
                (_, parameters) => { apply(parameters.Value<double>("value")); return ReactorActionResult.Success(); });
            return node;
        }

        public static ReactorActionNode ConfirmedAction(ReactorExtensionBuilder builder, string id,
            string label, string explanation, ReactorActionHandler apply)
        {
            builder.AddAction(new ReactorActionDescriptor(id, label, ReactorActionRisk.Gameplay,
                requiresConfirmation: true, description: explanation), apply);
            return new ReactorActionNode(id, label, id, explanation);
        }

        public static IEnumerable<ReactorMenuNode> BoundRows(string actionId, int count)
        {
            if (count < 1 || count > 256) throw new ArgumentOutOfRangeException(nameof(count));
            return Enumerable.Range(1, count).Select(index => new ReactorActionNode(
                "item-" + index, "Example item " + index, actionId,
                "An in-memory selection only; this does not spawn or purchase anything.", true, true,
                new JObject { ["item"] = index }));
        }
    }
}
