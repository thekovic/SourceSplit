﻿using LiveSplit.SourceSplit.GameHandling;

namespace LiveSplit.SourceSplit.GameSpecific.HL2Mods
{
    class UpmineStruggle : GameSupport
    {
        public UpmineStruggle()
        {
            AddFirstMap("twhl_upmine_struggle");
            StartOnFirstLoadMaps.AddRange(FirstMaps);
            AddLastMap("twhl_upmine_struggle");

            WhenOutputIsQueued(ActionType.AutoEnd, "no_vo");
        }
    }
}
