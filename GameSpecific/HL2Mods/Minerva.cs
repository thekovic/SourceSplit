﻿using LiveSplit.ComponentUtil;
using System.Diagnostics;
using LiveSplit.SourceSplit.GameHandling;
using LiveSplit.SourceSplit.Utilities;
using System.IO;
using LiveSplit.UI.LayoutSavers;

namespace LiveSplit.SourceSplit.GameSpecific.HL2Mods
{
    class Minerva : GameSupport
    {
        // start: when view entity index switches from intro camera to player
        // ending: when end relay is queued (when screen begins flashes to black before fading out)

        public Minerva()
        {
            AddFirstMap("metastasis_1");
            AddLastMap("metastasis_4b");

            WhenCameraSwitchesToPlayer(ActionType.AutoStart, "intro-heli-camera");
            WhenOutputIsQueued(ActionType.AutoEnd, "outro-start");
        }
    }
}
