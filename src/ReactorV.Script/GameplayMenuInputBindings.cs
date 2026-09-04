using System.Collections.Generic;

namespace RageWebUI.Script
{
    /// <summary>
    /// One GTA frontend control and the stable semantic action exposed to the
    /// browser. Keeping this table independent of ScriptHook types makes the
    /// controller contract cheap to regression-test without loading GTA.
    /// </summary>
    internal readonly struct GameplayMenuControlBinding
    {
        internal GameplayMenuControlBinding(int control, string action)
        {
            Control = control;
            Action = action;
        }

        internal int Control { get; }
        internal string Action { get; }
    }

    internal static class GameplayMenuInputBindings
    {
        internal const int FrontendPauseControl = 199;
        internal const int FrontendPauseAlternateControl = 200;
        internal const int GameplayAttackControl = 24;
        internal const int CursorAcceptControl = 237;
        internal const int CursorCancelControl = 238;
        internal const int CursorScrollUpControl = 241;
        internal const int CursorScrollDownControl = 242;

        private static readonly int[] SuppressedControlGroups = { 0, 1, 2 };

        // GTA.Control numeric values are stable native control ids. These are
        // the same defaults used by ALLIN1 0.5's ControllerBindings contract.
        private static readonly GameplayMenuControlBinding[] Bindings =
        {
            new GameplayMenuControlBinding(188, "navigate-up"),
            new GameplayMenuControlBinding(187, "navigate-down"),
            new GameplayMenuControlBinding(189, "navigate-left"),
            new GameplayMenuControlBinding(190, "navigate-right"),
            new GameplayMenuControlBinding(201, "accept"),
            new GameplayMenuControlBinding(202, "back"),
            new GameplayMenuControlBinding(205, "previous-page"),
            new GameplayMenuControlBinding(206, "next-page"),
            new GameplayMenuControlBinding(207, "previous-category"),
            new GameplayMenuControlBinding(208, "next-category"),
            new GameplayMenuControlBinding(204, "filter-next"),
            new GameplayMenuControlBinding(203, "search"),
            new GameplayMenuControlBinding(191, "favorite"),
        };

        private static readonly int[] NeutralityControls =
        {
            188, 187, 189, 190, 201, 202, 205, 206, 207, 208,
            204, 203, 191,
            FrontendPauseControl,
            FrontendPauseAlternateControl,
            CursorAcceptControl,
            CursorCancelControl,
            CursorScrollUpControl,
            CursorScrollDownControl,
        };

        internal static IReadOnlyList<GameplayMenuControlBinding> All => Bindings;

        internal static IReadOnlyList<int> ControlGroups => SuppressedControlGroups;

        internal static IReadOnlyList<int> RelevantControls => NeutralityControls;

        /// <summary>
        /// A semantic mouse action belongs to the disabled-control down edge.
        /// Sampling the previous state while the menu is non-interactive keeps
        /// a button that was already held during gameplay from becoming a
        /// synthetic click when the overlay takes input ownership.
        /// </summary>
        internal static bool IsButtonPressEdge(bool isDown, bool wasDown) =>
            isDown && !wasDown;

        /// <summary>
        /// Windows and GTA can report the same physical secondary-button edge
        /// on one frame. The Windows edge is authoritative because it honors
        /// the user's swapped-button setting; suppress only the duplicate GTA
        /// Back semantic while leaving every other binding untouched.
        /// </summary>
        internal static bool ShouldEmitGameSemanticAction(
            string action,
            bool physicalSecondaryBackPosted) =>
            !physicalSecondaryBackPosted || action != "back";
    }
}
