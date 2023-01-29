﻿using LiveSplit.SourceSplit.GameHandling;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveSplit.SourceSplit.GameSpecific.HL2Mods
{
    class CallIn : GameSupport
    {
        // start:   when the entity that freezes the player is killed
        // end:     when weapons are stripped and hud disappears as a result

        public CallIn()
        {
            AddFirstMap("amap");
            AddLastMap("emap");

            WhenEntityIsKilled(ActionType.AutoStart, "clip1");
            WhenOutputIsFired(ActionType.AutoEnd, "strip", "StripWeaponsAndSuit");
        }
    }
}
