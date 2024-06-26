﻿using LiveSplit.SourceSplit.GameHandling;

namespace LiveSplit.SourceSplit.GameSpecific.HL2Mods
{
    class Daylight : GameSupport
    {
        // start: on first map
        // ending: when output to disconnect fires

        public Daylight()
        {
            AddFirstMap("ep2_aquaduct-1_5_test");
            StartOnFirstLoadMaps.AddRange(FirstMaps);
            AddLastMap("ep2_aquaduct-4_5");

            WhenDisconnectOutputFires(ActionType.AutoEnd, "end_disconnect", "command", "disconnect");
        }
    }
}
