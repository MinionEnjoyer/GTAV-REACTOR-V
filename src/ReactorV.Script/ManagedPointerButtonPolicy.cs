using System;

namespace RageWebUI.Script
{
    [Flags]
    internal enum ManagedPointerButtonSource
    {
        None = 0,
        CursorAccept = 1,
        GameplayAttack = 2,
        PhysicalLeftButton = 4,
    }

    internal readonly struct ManagedPointerButtonDecision
    {
        internal ManagedPointerButtonDecision(
            bool pressed,
            bool released,
            bool down,
            ManagedPointerButtonSource sources)
        {
            Pressed = pressed;
            Released = released;
            Down = down;
            Sources = sources;
        }

        internal bool Pressed { get; }
        internal bool Released { get; }
        internal bool Down { get; }
        internal ManagedPointerButtonSource Sources { get; }
    }

    /// <summary>
    /// Merges GTA's cursor-accept and attack aliases with a narrowly scoped
    /// physical left-button fallback. Different providers can report the same
    /// click a frame apart, so an owned press remains down until every source
    /// is neutral. A complete neutral sample is then required before rearming.
    /// This preserves real double-clicks while suppressing delayed duplicate
    /// edges from a second provider.
    /// </summary>
    internal sealed class ManagedPointerButtonPolicy
    {
        private bool _active;
        private bool _combinedDown;
        private bool _ownsPress;
        private bool _armedForPress;
        private ManagedPointerButtonSource _pressSources;

        internal ManagedPointerButtonDecision Observe(
            bool eligible,
            bool cursorAcceptDown,
            bool gameplayAttackDown,
            bool physicalLeftButtonDown,
            bool physicalPressedSinceLastSample)
        {
            if (!eligible)
            {
                Reset();
                return default;
            }

            var sources = ManagedPointerButtonSource.None;
            if (cursorAcceptDown)
                sources |= ManagedPointerButtonSource.CursorAccept;
            if (gameplayAttackDown)
                sources |= ManagedPointerButtonSource.GameplayAttack;
            if (physicalLeftButtonDown || physicalPressedSinceLastSample)
                sources |= ManagedPointerButtonSource.PhysicalLeftButton;

            var combinedDown = cursorAcceptDown ||
                gameplayAttackDown ||
                physicalLeftButtonDown;

            if (!_active)
            {
                // A click already held when the lease becomes interactive did
                // not begin in Reactor. Seed it without ever manufacturing a
                // down or its later unpaired release.
                _active = true;
                _combinedDown = combinedDown;
                _ownsPress = false;
                _armedForPress = !combinedDown && !physicalPressedSinceLastSample;
                _pressSources = ManagedPointerButtonSource.None;
                return default;
            }

            if (!_combinedDown && !combinedDown && physicalPressedSinceLastSample)
            {
                if (!_armedForPress)
                    return default;

                // GetAsyncKeyState's transition bit preserves a complete tap
                // that started and ended between two script ticks.
                _armedForPress = false;
                return new ManagedPointerButtonDecision(
                    pressed: true,
                    released: true,
                    down: false,
                    sources);
            }

            if (!_combinedDown && combinedDown)
            {
                _combinedDown = true;
                if (!_armedForPress)
                    return default;

                _ownsPress = true;
                _armedForPress = false;
                _pressSources = sources;
                return new ManagedPointerButtonDecision(
                    pressed: true,
                    released: false,
                    down: true,
                    sources);
            }

            if (_combinedDown && !combinedDown)
            {
                _combinedDown = false;
                if (!_ownsPress)
                {
                    _armedForPress = false;
                    return default;
                }

                _ownsPress = false;
                _armedForPress = false;
                var releaseSources = _pressSources;
                _pressSources = ManagedPointerButtonSource.None;
                return new ManagedPointerButtonDecision(
                    pressed: false,
                    released: true,
                    down: false,
                    releaseSources);
            }

            if (!combinedDown && !_ownsPress)
                _armedForPress = true;

            return new ManagedPointerButtonDecision(
                pressed: false,
                released: false,
                down: _ownsPress && combinedDown,
                sources: _ownsPress ? _pressSources | sources : sources);
        }

        internal void Reset()
        {
            _active = false;
            _combinedDown = false;
            _ownsPress = false;
            _armedForPress = false;
            _pressSources = ManagedPointerButtonSource.None;
        }
    }
}
