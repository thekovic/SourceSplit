﻿using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using LiveSplit.ComponentUtil;

namespace LiveSplit.SourceSplit.GameSpecific
{
    class BMSRetail : GameSupport
    {
        // how to match with demos:
        // start: first tick when your position is at 113 -1225 582 (cl_showpos 1)
        // ending: first tick nihilanth's health is zero

        private bool _onceFlag;
        private Vector3f _startPos = new Vector3f(113f, -1225f, 582f);
        private IntPtr _nihiPtr;
        private int _baseEntityHealthOffset = -1;
        private bool _resetFlag;

        public BMSRetail()
        {
            this.GameTimingMethod = GameTimingMethod.EngineTicksWithPauses;
            this.FirstMap = "bm_c1a0a";
            this.LastMap = "bm_c4a4a";
            this.RequiredProperties = PlayerProperties.Position;
        }

        public override void OnGameAttached(GameState state)
        {
            ProcessModuleWow64Safe server = state.GameProcess.ModulesWow64Safe().FirstOrDefault(x => x.ModuleName.ToLower() == "server.dll");
            Trace.Assert(server != null);

            var scanner = new SignatureScanner(state.GameProcess, server.BaseAddress, server.ModuleMemorySize);

            if (GameMemory.GetBaseEntityMemberOffset("m_iHealth", state.GameProcess, scanner, out _baseEntityHealthOffset))
                Debug.WriteLine("CBaseEntity::m_iHealth offset = 0x" + _baseEntityHealthOffset.ToString("X"));
        }

        public override void OnTimerReset(bool resetflagto)
        {
            _resetFlag = resetflagto;
        }

        public override void OnSessionStart(GameState state)
        {
            base.OnSessionStart(state);

            _onceFlag = false;

            if (this.IsLastMap && state.PlayerEntInfo.EntityPtr != IntPtr.Zero)
            {
                _nihiPtr = state.GetEntityByName("nihilanth", true);
                Debug.WriteLine("Nihilanth pointer = 0x" + _nihiPtr.ToString("X"));
            }
        }

        public override GameSupportResult OnUpdate(GameState state)
        {
            if (_onceFlag)
                return GameSupportResult.DoNothing;

            // map starts
            if (this.IsFirstMap && !_resetFlag && state.PlayerPosition.DistanceXY(_startPos) <= 1.0f)
            {
                Debug.WriteLine("black mesa start");
                _resetFlag = true;
                _onceFlag = true;
                return GameSupportResult.PlayerGainedControl;
            }
            else if (this.IsLastMap && _nihiPtr != IntPtr.Zero)
            {
                int nihiHealth;
                state.GameProcess.ReadValue(_nihiPtr + _baseEntityHealthOffset, out nihiHealth);
                if (nihiHealth <= 0)
                {
                    Debug.WriteLine("black mesa end");
                    _onceFlag = true;
                    return GameSupportResult.PlayerLostControl;
                }
            }
            return GameSupportResult.DoNothing;
        }
    }
}