﻿using System.Diagnostics;

namespace LiveSplit.SourceSplit.GameSpecific
{
    class HL2Mods_Hangover : GameSupport
    {
        // start: on first map
        // ending: when the final output is fired

        private bool _onceFlag;
        private float _splitTime;

        public HL2Mods_Hangover()
        {
            this.GameTimingMethod = GameTimingMethod.EngineTicksWithPauses;
            this.FirstMap = "hangover_00";
            this.LastMap = "hangover_02";
            this.StartOnFirstLoadMaps.Add(this.FirstMap);
            this.RequiredProperties = PlayerProperties.ViewEntity;
        }

        public override void OnSessionStart(GameState state)
        {
            base.OnSessionStart(state);
            if (IsLastMap)
                _splitTime = state.FindOutputFireTime("credits_weaponstrip", 10);
            _onceFlag = false;
        }

        public override GameSupportResult OnUpdate(GameState state)
        {
            if (_onceFlag)
                return GameSupportResult.DoNothing;

            if (this.IsLastMap)
            {
                float splitTime = state.FindOutputFireTime("credits_weaponstrip", 10);
                try
                {
                    if (splitTime == 0f && _splitTime != 0f)
                    {
                        _onceFlag = true;
                        Debug.WriteLine("hangover end");
                        return GameSupportResult.PlayerLostControl;
                    }
                }
                finally
                {
                    _splitTime = splitTime;
                }
            }
            return GameSupportResult.DoNothing;
        }
    }
}
