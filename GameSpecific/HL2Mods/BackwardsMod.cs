﻿using LiveSplit.SourceSplit.GameHandling;

namespace LiveSplit.SourceSplit.GameSpecific.HL2Mods
{
    class BackwardsMod : GameSupport
    {
        public BackwardsMod()
        {
            AddFirstMap("backward_d3_breen_01");
            StartOnFirstLoadMaps.AddRange(FirstMaps);
        }
    }
}
