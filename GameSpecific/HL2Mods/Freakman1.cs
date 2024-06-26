﻿using LiveSplit.SourceSplit.GameHandling;

namespace LiveSplit.SourceSplit.GameSpecific.HL2Mods
{
    class Freakman1 : GameSupport
    {
        // start: when the start trigger is hit
        // ending: when kleiner's hp is <= 0

        public Freakman1()
        {
            this.AddFirstMap("gordon1");
            StartOnFirstLoadMaps.AddRange(FirstMaps);
            this.AddLastMap("endbattle");

            Templates.Add(new EntityMurdered
            (
                this, ActionType.AutoEnd,
                (s) => s.GameEngine.GetEntityByPos(0f, 0f, 1888f, 1f)
            ));
        }
    }
}
