﻿using LiveSplit.SourceSplit.GameHandling;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveSplit.SourceSplit.GameSpecific.HL2Mods
{
    class Exit2 : GameSupport
    {
        public Exit2()
        {
            AddFirstMap("e2_01");
            AddLastMap("e2_07");

            WhenCameraSwitchesToPlayer(ActionType.AutoStart, "view");
            WhenCameraSwitchesFromPlayer(ActionType.AutoEnd, "view");
        }
    }
}
