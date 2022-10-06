﻿using System;
using System.Diagnostics;
using LiveSplit.SourceSplit.GameHandling;

namespace LiveSplit.SourceSplit.GameSpecific
{
    class PortalMods_PortalPro : Portal
    {
        // how to match this timing with demos:
        // start: on view entity changing from start camera's to the player's
        // ending: on view entity changing from the player's to final camera's

        private bool _onceFlag;

        private int _startCamIndex;
        private int _endCamIndex;

        public PortalMods_PortalPro() : base()
        {
            this.AddFirstMap("start");
            this.AddLastMap("boss");
             
        }

        public override void OnSessionStart(GameState state, TimerActions actions)
        {
            base.OnSessionStart(state, actions);

            if (this.IsFirstMap)
            {
                _startCamIndex = state.GameEngine.GetEntIndexByName("wub_viewcontrol");
                //Debug.WriteLine("found start cam index at " + _startCamIndex);
            }

            if (this.IsLastMap && state.PlayerEntInfo.EntityPtr != IntPtr.Zero)
            {
                _endCamIndex = state.GameEngine.GetEntIndexByName("end_game_camera");
                //Debug.WriteLine("found end cam index at " + _endCamIndex);
            }

            _onceFlag = false;
        }

        public override void OnUpdate(GameState state, TimerActions actions)
        {
            if (_onceFlag)
                return;

            if (this.IsFirstMap)
            {
                if (state.PlayerViewEntityIndex.Old == _startCamIndex && state.PlayerViewEntityIndex.Current == 1)
                {
                    Debug.WriteLine("portal pro start");
                    _onceFlag = true;
                    actions.Start(StartOffsetTicks); return;
                }
            }
            else if (this.IsLastMap)
            {
                if (state.PlayerViewEntityIndex.Old == 1 && state.PlayerViewEntityIndex.Current == _endCamIndex)
                {
                    Debug.WriteLine("portal pro end");
                    _onceFlag = true;
                    actions.End(EndOffsetTicks); return;
                }
            }

            return;
        }

    }
}
