﻿using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiveSplit.ComponentUtil;
using LiveSplit.SourceSplit.GameSpecific;
using LiveSplit.SourceSplit.Extensions;
using LiveSplit.SourceSplit.Utils;

namespace LiveSplit.SourceSplit
{
    class GameMemory
    {
        [DllImport("winmm.dll")]
        static extern uint timeBeginPeriod(uint uMilliseconds);
        [DllImport("winmm.dll")]
        static extern uint timeEndPeriod(uint uMilliseconds);

        public event EventHandler<SetTimingMethodEventArgs> OnSetTimingMethod;
        public event EventHandler<SetTickRateEventArgs> OnSetTickRate;
        public event EventHandler<SessionTicksUpdateEventArgs> OnSessionTimeUpdate;
        public event EventHandler<PlayerControlChangedEventArgs> OnPlayerGainedControl;
        public event EventHandler<PlayerControlChangedEventArgs> OnPlayerLostControl;
        public event EventHandler<PlayerControlChangedEventArgs> ManualSplit;
        public event EventHandler<MapChangedEventArgs> OnMapChanged;
        public event EventHandler<SessionStartedEventArgs> OnSessionStarted;
        public event EventHandler<MiscTimeEventArgs> OnMiscTime;
        public event EventHandler OnSessionEnded;
        public event EventHandler OnNewGameStarted;

        private Task _thread;
        private SynchronizationContext _uiThread;
        private CancellationTokenSource _cancelSource;

        private SigScanTarget _curTimeTarget;
        private SigScanTarget _signOnStateTarget1;
        private SigScanTarget _signOnStateTarget2;
        private SigScanTarget _signOnStateTarget3;
        private SigScanTarget _curMapTarget;
        private SigScanTarget _globalEntityListTarget;
        private SigScanTarget _gameDirTarget;
        private SigScanTarget _hostStateTarget;
        private SigScanTarget _serverStateTarget;
        private SigScanTarget _serverStateTarget2;
        private SigScanTarget _fadeListTarget;
        private SigScanTarget _eventQueueTarget;
        private SigScanTarget _eventQueueTarget2;
        private SigScanTarget _eventQueueTarget3;

        private SigScanTarget _infraIsLoadingTarget;
        private MemoryWatcher<byte> _infraIsLoading;
        private bool _isInfra = false;

        public static bool IsSource2003 = false;

        private bool _gotTickRate;

        private int _timesOver;
        private int _timeOverSpent;

        private SourceSplitSettings _settings;

        private ValueWatcher<int> _custTimeCountWatcher;
        private IntPtr _custTimeCountPtr;
        private Detour _custTimeInjection;

        public GameState _state;

        // TODO: match tickrate as closely as possible without going over
        // otherwise we will most likely read when the game isn't sleeping
        // must also account for variance of windows scheduler
        private const int TARGET_UPDATE_RATE = 13;

        public GameMemory(SourceSplitSettings settings)
        {
            _settings = settings;

            #region SIGNATURES
            // detect game offsets in a game/version-independent way by scanning for code signatures

            // TODO: refine hl2 2014 signatures once an update after the may 29th one is released

            _infraIsLoadingTarget = new SigScanTarget();

            // \x80\x3D\x2A\x2A\x2A\x2A\x00\x0F\x84\x2A\x2A\x2A\x2A\x56\xC6\x05\x2A\x2A\x2A\x2A\x00
            _infraIsLoadingTarget.AddSignature(2,
                "80 3D ?? ?? ?? ?? 00",     // CMP  loadingbyte,0x0
                "0F 84 ?? ?? ?? ??",        // JZ   0x10028ebb
                "56",                       // PUSH ESI
                "C6 05 ?? ?? ?? ?? 00");    // MOV  byte ptr [0x10458c04],0x0
            // \x80\x3D\x2A\x2A\x2A\x2A\x00\x0F\x84\x2A\x2A\x2A\x2A\x56\x57\xC6\x05\x2A\x2A\x2A\x2A\x00
            _infraIsLoadingTarget.AddSignature(2,
                "80 3D ?? ?? ?? ?? 00",     // CMP  loadingbyte,0x0
                "0F 84 ?? ?? ?? ??",        // JZ   0x1002b7db
                "56",                       // PUSH ESI
                "57",                       // PUSH EDI
                "C6 05 ?? ?? ?? ?? 00");    // MOV  byte ptr [0x1047ccd4],0x0

            // CViewEffects::m_FadeList (g_ViewEffects)
            _fadeListTarget = new SigScanTarget();
            _fadeListTarget.OnFound = (proc, scanner, ptr) => !proc.ReadPointer(ptr, out ptr) ? IntPtr.Zero : ptr;
            
            // infra
            _fadeListTarget.AddSignature(2, 
                "8D 88 ?? ?? ?? ??",        // LEA ECX,[EAX + fadeList]
                "8B 01",                    // MOV EAX,dword ptr [ECX]
                "8B 40 ??",                 // MOV EAX,dword ptr [EAX + 0xc]
                "8D 55 ??");                // LEA EDX,[EBP + -0x2c]


            // CBaseServer::(server_state_t)m_State
            _serverStateTarget = new SigScanTarget();
            _serverStateTarget.OnFound = (proc, scanner, ptr) => !proc.ReadPointer(ptr, out ptr) ? IntPtr.Zero : ptr;
            // works for every engine.dll
            // \x83\xf8\x01\x0f\x8c..\x00\x00\x3d\x00\x02\x00\x00\x0f\x8f..\x00\x00\x83\x3d(....)\x02\x7d
            _serverStateTarget.AddSignature(22,
                "83 F8 01",                // cmp     eax, 1
                "0F 8C ?? ?? 00 00",       // jl      loc_200087FB
                "3D 00 02 00 00",          // cmp     eax, 200h
                "0F 8F ?? ?? 00 00",       // jg      loc_200087FB
                "83 3d ?? ?? ?? ?? 02",    // cmp     m_State, 2
                "7D");                     // jge     short loc_200085FD

            // except HLS OE...
            // \x83\x3D\x2A\x2A\x2A\x2A\x02\xA1\x2A\x2A\x2A\x2A\x7D\x2A\xA1\x2A\x2A\x2A\x2A\x89\x86\x2A\x2A\x2A\x2A
            _serverStateTarget.AddSignature(2,
                "83 3D ?? ?? ?? ?? 02",     // CMP  state,0x2
                "A1 ?? ?? ?? ??",           // MOV  EAX,[0x20554d0c]
                "7D ??",                    // JGE  0x2006811c
                "A1 ?? ?? ?? ??",           // MOV  EAX,[0x2037cf68]
                "89 86 ?? ?? ?? ??");       // MOV  dword ptr [ESI + 0x220],EAX

            // and HL2SURVIVOR
            // \x83\x3D\x2A\x2A\x2A\x2A\x02\x7C\x2A\x8B\x15\x2A\x2A\x2A\x2A
            _serverStateTarget.AddSignature(2,
                "83 3D ?? ?? ?? ?? 02",     // CMP  state,0x2
                "7C ??",                    // JL   0x200117d6
                "8B 15 ?? ?? ?? ??");       // MOV  EDX,dword ptr [0x203c2abc]

            // and source 2003 leak...
            _serverStateTarget2 = new SigScanTarget();
            _serverStateTarget2.OnFound = (proc, scanner, ptr) => {
                IsSource2003 = true;
                return !proc.ReadPointer(ptr, out ptr) ? IntPtr.Zero : ptr; };

            // state (old 2003 naming)
            // \xB9\x2A\x2A\x2A\x2A\xE8\x2A\x2A\x2A\x2A\xD9\x1D\x2A\x2A\x2A\x2A\xA1\x2A\x2A\x2A\x2A\x8B\x38
            _serverStateTarget2.AddSignature(1,
                "B9 ?? ?? ?? ??",          // MOV     ECX, state
                "E8 ?? ?? ?? ??",          // CALL    0x200fecb0
                "D9 1D ?? ?? ?? ??",       // FSTP    dword ptr [0x207c9f44]
                "A1 ?? ?? ?? ??",          // MOV     EAX,[0x20a40e5c]
                "8B 38");                  // MOV     EDI,dword ptr [EAX]

            // TODO: find a generic curTime signature
            // frameTime->curtime WIP sig, 76% success
            // \xe8...\x00\xd9\x1d....\x8b\x0d....\x8b\x11

            // CGlobalVarsBase::curtime (g_ClientGlobalVariables aka gpGlobals)
            _curTimeTarget = new SigScanTarget();
            _curTimeTarget.OnFound = (proc, scanner, ptr) => proc.ReadPointer(ptr, out ptr) ? ptr : IntPtr.Zero;
            // orange box and older
            // \xa3....\xb9....\xa3....\xe8....\xd9\x1d(....)\xb9....\xe8....\xd9\x1d
            _curTimeTarget.AddSignature(22,
                "A3 ?? ?? ?? ??",          // mov     dword_2038BA6C, eax
                "B9 ?? ?? ?? ??",          // mov     ecx, offset unk_2038B8E8
                "A3 ?? ?? ?? ??",          // mov     dword_2035DDA4, eax
                "E8 ?? ?? ?? ??",          // call    sub_20048110
                "D9 1D ?? ?? ?? ??",       // fstp    curTime
                "B9 ?? ?? ?? ??",          // mov     ecx, offset unk_2038B8E8
                "E8 ?? ?? ?? ??",          // call    sub_20048130
                "D9 1D");                  // fstp    frametime

            // portal 2
            // \x89\x96\xc4\x00\x00\x00\x8b\x86\xc8\x00\x00\x00\x8b\xce\xa3....\xe8....\xd9\x1d(....)\x8b\xce\xe8....\xd9\x1d
            _curTimeTarget.AddSignature(26,
                "89 96 C4 00 00 00",       // mov     [esi+0C4h], edx
                "8B 86 C8 00 00 00",       // mov     eax, [esi+0C8h]
                "8B CE",                   // mov     ecx, esi
                "A3 ?? ?? ?? ??",          // mov     dword_10414AD0, eax
                "E8 ?? ?? ?? ??",          // call    sub_100A0F30
                "D9 1D ?? ?? ?? ??",       // fstp    curTime
                "8B CE",                   // mov     ecx, esi
                "E8 ?? ?? ?? ??",          // call    sub_100A0FB0
                "D9 1D");                  // fstp    frametime

            // source 2009
            // \x89\x8f\xc4\x00\x00\x00\x8b\x97\xc8\x00\x00\x00\x8b\xcf\x89\x15....\xe8....\xd9\x1d(....)\x8b\xcf\xe8....\xd9\x1d
            _curTimeTarget.AddSignature(27,
                "89 8F C4 00 00 00",       // mov     [edi+0C4h], ecx
                "8B 97 C8 00 00 00",       // mov     edx, [edi+0C8h]
                "8B CF",                   // mov     ecx, edi
                "89 15 ?? ?? ?? ??",       // mov     dword_10422624, edx
                "E8 ?? ?? ?? ??",          // call    sub_1008FE40
                "D9 1D ?? ?? ?? ??",       // fstp    curTime
                "8B CF",                   // mov     ecx, edi
                "E8 ?? ?? ?? ??",          // call    sub_1008FEB0
                "D9 1D");                  // fstp    flt_1042261C

            // hl2 may 29 2014 update
            // \xa3....\x89\x15....\xe8....\xd9\x1d....\x57\xb9....\xe8....\x8b\x0d....\xd9\x1d
            _curTimeTarget.AddSignature(18,
                "A3 ?? ?? ?? ??",          // mov     dword_103B4AC8, eax
                "89 15 ?? ?? ?? ??",       // mov     dword_10452F38, edx
                "E8 ?? ?? ?? ??",          // call    sub_100CE610
                "D9 1D ?? ?? ?? ??",       // fstp    curTime
                "57",                      // push    edi
                "B9 ?? ?? ?? ??",          // mov     ecx, offset unk_10452D98
                "E8 ?? ?? ?? ??",          // call    sub_100CE390
                "8B 0D ?? ?? ?? ??",       // mov     ecx, dword_1043686C
                "D9 1D");                  // fstp    frametime
            // bms retail
            // \xa3....\x89\x15....\xe8....\xd9\x1d....\xb9....\xe8....\x8b\x0d....\xd9\x1d
            _curTimeTarget.AddSignature(18,
                "A3 ?? ?? ?? ??",          // mov     dword_103B4AC8, eax
                "89 15 ?? ?? ?? ??",       // mov     dword_10452F38, edx
                "E8 ?? ?? ?? ??",          // call    sub_100CE610
                "D9 1D ?? ?? ?? ??",       // fstp    curTime
                "B9 ?? ?? ?? ??",          // mov     ecx, offset unk_10452D98
                "E8 ?? ?? ?? ??",          // call    sub_100CE390
                "8B 0D ?? ?? ?? ??",       // mov     ecx, dword_1043686C
                "D9 1D");                  // fstp    frametime
            // source 2003 leak
            // \xA3\x2A\x2A\x2A\x2A\xE8\x2A\x2A\x2A\x2A\xD9\x1D\x2A\x2A\x2A\x2A\x8B\x44\x24\x2A
            _curTimeTarget.AddSignature(12,
                "A3 ?? ?? ?? ??",          // MOV     intervalpertick,EAX
                "E8 ?? ?? ?? ??",          // CALL    0x20034da0
                "D9 1D ?? ?? ?? ??",       // FSTP    curtime
                "8B 44 24 ??");            // MOV     EAX,dword ptr [ESP + 0x48]
            // HL2SURVIVOR
            // \xA1\x2A\x2A\x2A\x2A\x7D\x2A\xA1\x2A\x2A\x2A\x2A\x89\x81\x2A\x2A\x2A\x2A
            _curTimeTarget.AddSignature(4,
                "F3 0F 11 05 ?? ?? ?? ??",
                "8B 01",
                "52",
                "FF 50 ??",
                "8B 0D ?? ?? ?? ??");
            // sin episodes: emergence
            // \xA1\x2A\x2A\x2A\x2A\x7D\x2A\xA1\x2A\x2A\x2A\x2A\x89\x81\x2A\x2A\x2A\x2A
            _curTimeTarget.AddSignature(8,
                "D8 0D ?? ?? ?? ??",    // FMUL     dword ptr [0x2079da04]
                "D9 1D ?? ?? ?? ??",    // FSTP     dword ptr [DAT_2076ea0c]
                "8B 01",                // MOV      EAX,dword ptr [ECX]
                "FF 50 ??",             // CALL     dword ptr [EAX + 0x8]
                "8B 15 ?? ?? ?? ??");   // MOV      EDX,dword ptr [0x20349650]

            // CBaseClientState::m_nSignOnState (older engines)
            _signOnStateTarget1 = new SigScanTarget();
            _signOnStateTarget1.OnFound = (proc, scanner, ptr) => proc.ReadPointer(ptr, out ptr) ? ptr : IntPtr.Zero;
            // orange box and older (and bms retail)
            // \x80\x3d....\x00\x74\x06\xb8....\xc3\x83\x3d(....)\x02\xb8
            _signOnStateTarget1.AddSignature(17,
                "80 3D ?? ?? ?? ?? 00",    // cmp     byte_698EE114, 0
                "74 06",                   // jz      short loc_6936C8FF
                "B8 ?? ?? ?? ??",          // mov     eax, offset aDedicatedServe ; "Dedicated Server"
                "C3",                      // retn
                "83 3D ?? ?? ?? ?? 02",    // cmp     CBaseClientState__m_nSignonState, 2
                "B8 ?? ?? ?? ??");         // mov     eax, offset MultiByteStr
            // orange box and older (and bms retail)
            // \x80\x3d....\x00\x74\x06\xb8....\xc3\x83\x3d(....)\x02\xb8
            _signOnStateTarget1.AddSignature(17,
                "80 3D ?? ?? ?? ?? 00",    // cmp     byte_698EE114, 0
                "74 06",                   // jz      short loc_6936C8FF
                "B8 ?? ?? ?? ??",          // mov     eax, offset aDedicatedServe ; "Dedicated Server"
                "C3",                      // retn
                "83 3D ?? ?? ?? ?? 02",    // cmp     CBaseClientState__m_nSignonState, 2
                "B8 ?? ?? ?? ??");         // mov     eax, offset MultiByteStr

            // source 2003 leak
            // \xA1\x2A\x2A\x2A\x2A\x85\xC0\x75\x2A\xB8\x2A\x2A\x2A\x2A
            _signOnStateTarget1.AddSignature(1,
                "A1 ?? ?? ?? ??",          // MOV     EAX, state
                "85 C0",                   // TEST    EAX,EAX
                "75 ??",                   // JNZ     0x2001492f
                "B8 ?? ?? ?? ??");         // MOV     EAX,0x20193f74


            _signOnStateTarget3 = new SigScanTarget("Dedicated Server\0".ConvertToHex());
            _signOnStateTarget3.OnFound = (proc, scanner, ptr) =>
            {
                string ptrByteString = ptr.GetByteString();

                SigScanTarget newTarg = new SigScanTarget($"B8 {ptrByteString}");
                IntPtr insc = scanner.Scan(newTarg);
                if (insc == IntPtr.Zero)
                    return insc;

                SignatureScanner scanner2 = new SignatureScanner(proc, insc, 40);
                newTarg = new SigScanTarget(2, "83 3D ?? ?? ?? ?? 02");
                newTarg.OnFound = (p, s, pt) => 
                    p.ReadPointer(pt);
                return scanner2.Scan(newTarg);
            };

            // CBaseClientState::m_nSignOnState
            _signOnStateTarget2 = new SigScanTarget();
            _signOnStateTarget2.OnFound = (proc, scanner, ptr) => {
                if (!proc.ReadPointer(ptr, out ptr)) // deref instruction
                    return IntPtr.Zero;
                if (!proc.ReadPointer(ptr, out ptr)) // deref ptr
                    return IntPtr.Zero;
                return IntPtr.Add(ptr, 0x70); // this+0x70 = m_nSignOnState
            };
            // source 2009 / portal 2
            // \x74.\x8b\x74\x87\x04\x83\x7e\x18\x00\x74\x2d\x8b\x0d(....)\x8b\x49\x18
            _signOnStateTarget2.AddSignature(14,
                "74 ??",                   // jz      short loc_693D4E22
                "8B 74 87 04",             // mov     esi, [edi+eax*4+4]
                "83 7E 18 00",             // cmp     dword ptr [esi+18h], 0
                "74 2D",                   // jz      short loc_693D4DFC
                "8B 0D ?? ?? ?? ??",       // mov     ecx, baseclientstate
                "8B 49 18");               // mov     ecx, [ecx+18h]

            // CBaseServer::m_szMapname[64]
            _curMapTarget = new SigScanTarget();
            _curMapTarget.OnFound = (proc, scanner, ptr) => proc.ReadPointer(ptr, out ptr) ? ptr : IntPtr.Zero;
            // \x68(....).\xe8...\x00\x83\xc4\x08\x85\xc0\x0f\x84..\x00\x00\x83\xc7\x01\x83.\x50\x3b\x7e\x18\x7c
            // source 2006 and older
            _curMapTarget.AddSignature(1,
                "68 ?? ?? ?? ??",          // push    offset map
                "??",                      // push    ebp
                "E8 ?? ?? ?? 00",          // call    __stricmp
                "83 C4 08",                // add     esp, 8
                "85 C0",                   // test    eax, eax
                "0F 84 ?? ?? 00 00",       // jz      loc_200CDF8D
                "83 C7 01",                // add     edi, 1
                "83 ?? 50",                // add     ebp, 50h
                "3B 7E 18",                // cmp     edi, [esi+18h]
                "7C");                     // jl      short loc_200CDEC0
            // orange box and newer
            // \xd9.\x2c\xd9\xc9\xdf\xf1\xdd\xd8\x76.\x80.....\x00
            _curMapTarget.AddSignature(13,
                "D9 ?? 2C",                // fld     dword ptr [edx+2Ch]
                "D9 C9",                   // fxch    st(1)
                "DF F1",                   // fcomip  st, st(1)
                "DD D8",                   // fstp    st
                "76 ??",                   // jbe     short loc_6946F651
                "80 ?? ?? ?? ?? ?? 00");   // cmp     map, 0
            // bms retail
            // \xdd.....\xdc.....\xdf\xf1\xdd\xd8\x76.\x80.....\x00
            _curMapTarget.AddSignature(20,
                "DD ?? ?? ?? ?? ??",       // fld     [ebp+var_144]
                "DC ?? ?? ?? ?? ??",       // fsub    dbl_103F36D8
                "DF F1",                   // fcomip  st, st(1)
                "DD D8",                   // fstp    st
                "76 ??",                   // jbe     short loc_101B8F6F
                "80 ?? ?? ?? ?? ?? 00");   // cmp     map, 0
            // infra
            _curMapTarget.AddSignature(16,
                "68 ?? ?? ?? ??",          // push    0x103603e0
                "c6 ?? ?? ??",             // mov     byte ptr [EBP + -0x1],0x1
                "ff ??",                   // call    ESI
                "83 c4 ??",                // add     ESP,0x4
                "80 ?? ?? ?? ?? ?? 00",    // cmp     map, 0x0
                "B8 ?? ?? ?? ??");         // mov     EAX, map
            // HL2SURVIVOR
            //\x80\x3D\x2A\x2A\x2A\x2A\x00\x74\x2A\x8B\x01
            _curMapTarget.AddSignature(2,
                "80 3D ?? ?? ?? ?? 00",   // CMP    byte ptr [map],0x0
                "74 ?? ",                 // JZ     LAB_200b7839
                "8B 01");                 // MOV    EAX,dword ptr [ECX]=>PTR_PTR_FUN_202e5050

            // name[64] (old 2003 naming)
            // \xA0\x2A\x2A\x2A\x2A\x84\xC0\x74\x2A\xB8\x2A\x2A\x2A\x2A
            // source 2003 leak
            _curMapTarget.AddSignature(1,
                "A0 ?? ?? ?? ??",          // MOV     AL, name[64]
                "84 C0",                   // TEST    AL, AL
                "74 ??",                   // JZ      0x20090dca
                "B8 ?? ?? ?? ??");         // MOV     EAX,0x207cab64

            // CBaseEntityList::(CEntInfo)m_EntPtrArray
            _globalEntityListTarget = new SigScanTarget();
            // \x6a\x00\x6a\x00\x50\x6a\x00\xb9(....)\xe8
            // deref to get vtable ptr, add 4 to get start of entity list
            _globalEntityListTarget.OnFound = (proc, scanner, ptr) => proc.ReadPointer(ptr, out ptr) ? ptr + 4 : IntPtr.Zero;
            _globalEntityListTarget.AddSignature(8,
                "6A 00",                   // push    0
                "6A 00",                   // push    0
                "50",                      // push    eax
                "6A 00",                   // push    0
                "B9 ?? ?? ?? ??",          // mov     ecx, offset CGlobalEntityList_vtable_ptr
                "E8");                     // call    sub_22289800

            // CHostState::m_currentState
            _hostStateTarget = new SigScanTarget();
            // subtract 4 to get m_currentState
            _hostStateTarget.OnFound = (proc, scanner, ptr) => proc.ReadPointer(ptr, out ptr) ? ptr - 4 : IntPtr.Zero;
            // \xc7\x05....\x07\x00\x00\x00\xc3
            _hostStateTarget.AddSignature(2,
                "C7 05 ?? ?? ?? ?? 07 00 00 00", // mov     g_HostState_m_nextState, 7
                "C3");                           // retn

            // TODO: find better way to do this. multiple sigs instead?
            _gameDirTarget = new SigScanTarget(0, "25732F736176652F25732E736176"); // "%s/save/%s.sav"
            _gameDirTarget.OnFound = (proc, scanner, ptr) => {
                byte[] b = BitConverter.GetBytes(ptr.ToInt32());
                var target = new SigScanTarget(-4,
                    // push    offset aSMapsS_sav
                    $"68 {b[0]:X02} {b[1]:X02} {b[2]:X02} {b[3]:X02}");
                IntPtr ptrPtr = scanner.Scan(target);
                if (ptrPtr == IntPtr.Zero)
                    return IntPtr.Zero;
                IntPtr ret;
                proc.ReadPointer(ptrPtr, out ret);
                return ret;
            };

            // CEventQueue::m_Events
            _eventQueueTarget = new SigScanTarget();
            _eventQueueTarget.OnFound = (proc, scanner, ptr) => proc.ReadPointer(ptr, out ptr) ? ptr : IntPtr.Zero;
            // source 2007 and newer
            _eventQueueTarget.AddSignature(1,
                "A1 ?? ?? ?? ??",           // MOV  EAX,[m_Events]
                "85 C0",                    // TEST EAX,EAX
                "74 ??",                    // JZ   LAB_10425dd5
                "56",                       // PUSH ESI
                "8D 9B 00 00 00 00");       // LEA  EBX,[EBX]

            // source 2006
            _eventQueueTarget2 = new SigScanTarget();
            _eventQueueTarget2.OnFound = (proc, scanner, ptr) => proc.ReadPointer(ptr, out ptr) ? ptr + 0x30 : IntPtr.Zero;
            _eventQueueTarget2.AddSignature(1,
               "B9 ?? ?? ?? ??",            // MOV  ECX,DAT_22570180
               "E8 ?? ?? ?? ??",            // CALL FUN_22258f40
               "45");                       // INC EBP

            // source 2003
            _eventQueueTarget3 = new SigScanTarget();
            _eventQueueTarget3.OnFound = (proc, scanner, ptr) => proc.ReadPointer(ptr, out ptr) ? ptr + 0x64 : IntPtr.Zero;
            _eventQueueTarget3.AddSignature(1,
               "B9 ?? ?? ?? ??",            // MOV  ECX,DAT_2231d908
               "50",                        // PUSH EAX
               "E8 ?? ?? ?? ??",            // CALL FUN_2211c300
               "D9 46 ??");                 // FLD  DWORD PTR [ESI + 0xC]
            #endregion
        }
        ~GameMemory()
        {
            Debug.WriteLine("GameMemory finalizer");
        }
        public void StartReading()
        {
            if (_thread != null && _thread.Status == TaskStatus.Running)
                throw new InvalidOperationException();
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                throw new InvalidOperationException("SynchronizationContext.Current is not a UI thread.");

            _cancelSource = new CancellationTokenSource();
            _uiThread = SynchronizationContext.Current;
            _thread = Task.Factory.StartNew(() => MemoryReadThread(_cancelSource));
        }

        public void Stop()
        {
            if (_cancelSource == null || _thread == null || _thread.Status != TaskStatus.Running)
                return;

            _custTimeInjection?.Restore();

            _cancelSource.Cancel();
            _thread.Wait();
        }

        void MemoryReadThread(CancellationTokenSource cts)
        {
            // force windows timer resolution to 1ms. it probably already is though, from the game.
            timeBeginPeriod(1);
            // we do a lot of timing critical stuff so this may help out
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;

            while (true)
            {
                try
                {
                    Debug.WriteLine("Waiting for process");

                    Process game;
                    GameOffsets offsets;
                    while (!this.TryGetGameProcess(out game, out offsets))
                    {
                        Thread.Sleep(750);

                        if (cts.IsCancellationRequested)
                            goto ret;
                    }

                    this.HandleProcess(game, offsets, cts);

                    if (cts.IsCancellationRequested)
                        goto ret;
                }
                catch (Exception ex) // probably a Win32Exception on access denied to a process
                {
                    Trace.WriteLine(ex.ToString());
                    Thread.Sleep(1000);
                }
            }

        ret:

            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
            timeEndPeriod(1);
        }

        string GetGameDir(Process p, GameOffsets offsets)
        {
            string absoluteGameDir;
            p.ReadString(offsets.GameDirPtr, ReadStringType.UTF8, 260, out absoluteGameDir);

            switch (new DirectoryInfo(absoluteGameDir).Name.ToLower())
            {
                case "infra":
                    _isInfra = true;
                    break;
            }

            return absoluteGameDir;
        }

        // TODO: log fails
        bool TryGetGameProcess(out Process p, out GameOffsets offsets)
        {
#if DEBUG
            var sw = Stopwatch.StartNew();
#endif
            _isInfra = false;
            IsSource2003 = false;

            string[] procs = _settings.GameProcesses.Select(x => x.ToLower().Replace(".exe", String.Empty)).ToArray();
            p = Process.GetProcesses().FirstOrDefault(x => procs.Contains(x.ProcessName.ToLower()));
            offsets = new GameOffsets();

            if (p == null || p.HasExited || Util.IsVACProtectedProcess(p))
                return false;

            // process is up, check if engine and server are both loaded yet
            ProcessModuleWow64Safe engine = p.ModulesWow64Safe().FirstOrDefault(x => x.ModuleName.ToLower() == "engine.dll");
            ProcessModuleWow64Safe server = p.ModulesWow64Safe().FirstOrDefault(x => x.ModuleName.ToLower() == "server.dll");

            if (engine == null || server == null)
                return false;

            bool scanForPtr(ref IntPtr ptr, SigScanTarget target, SignatureScanner scanner, string ptrName, string sigName = " ")
            {
                bool found = (ptr = scanner.Scan(target)) != IntPtr.Zero;
                Debug.WriteLine($"{ptrName} = {(found ? $"0x{ptr.ToString("X")}" : "NOT FOUND")} through sig {sigName}");
                return found;
            }

            #region ENGINE
            // required engine stuff
            var scanner = new SignatureScanner(p, engine.BaseAddress, engine.ModuleMemorySize);

            if ((!scanForPtr(ref offsets.ServerStatePtr, _serverStateTarget, scanner, "CBaseServer::(server_state_t)m_State", "1")
                && !scanForPtr(ref offsets.ServerStatePtr, _serverStateTarget2, scanner, "CBaseServer::(server_state_t)m_State", "2"))
                || !scanForPtr(ref offsets.CurMapPtr, _curMapTarget, scanner, "CBaseServer::m_szMapname")
                || !scanForPtr(ref offsets.CurTimePtr, _curTimeTarget, scanner, "CGlobalVarsBase::curtime")
                || !scanForPtr(ref offsets.GameDirPtr, _gameDirTarget, scanner, "game dir")
                || !scanForPtr(ref offsets.HostStatePtr, _hostStateTarget, scanner, "host state")
                || !scanForPtr(ref offsets.CurMapPtr, _curMapTarget, scanner, "current map"))
                return false;

            if (!scanForPtr(ref offsets.SignOnStatePtr, _signOnStateTarget3, scanner, "CBaseClientState::m_nSignonState", "3")
                && !scanForPtr(ref offsets.SignOnStatePtr, _signOnStateTarget1, scanner, "CBaseClientState::m_nSignonState", "1")
                && !scanForPtr(ref offsets.SignOnStatePtr, _signOnStateTarget2, scanner, "CBaseClientState::m_nSignonState", "2"))
                return false;

            #region HOST_RUNFRAME TICK COUNT DETOUR
            // find the beginning of _host_runframe
            // find string pointer and reference
            SigScanTarget _hrfStringTarg = new SigScanTarget("_Host_RunFrame (top):  _heapchk() != _HEAPOK\n".ConvertToHex() + "00");
            IntPtr _hrfStringPtr = IntPtr.Zero;
            if (!scanForPtr(ref _hrfStringPtr, _hrfStringTarg, scanner, "host_runframe string pointer")
                || !scanForPtr(ref _hrfStringPtr, new SigScanTarget("68" + _hrfStringPtr.GetByteString()), scanner, "host_runframe string reference")
                // then find the target jl of the update loop
                || !scanForPtr(ref _hrfStringPtr, 
                    new SigScanTarget("0F 8C ?? ?? ?? FF"), 
                    new SignatureScanner(p, _hrfStringPtr, 0x700), 
                    "host_runframe target jump"))
                return false;

            // we can't detour the jl directly due to weird performance issues so back track until we
            // found our target cmp instruction

            // find out where the jl goes to
            IntPtr loopTo = _hrfStringPtr + p.ReadValue<int>(_hrfStringPtr + 0x2) + 0x6;
            // find cmp instruction
            int i = 0;
            // should never be more than 15 bytes away...
            for (i = 0; i < 15; i++)
            {
                byte curByte = p.ReadValue<byte>(_hrfStringPtr - i);
                // we are cmp'ing 2 registeres normally, so use 0x39 and 0x3B
                if (new byte[] { 0x39, 0x3B }.Contains(curByte))
                {
                    byte prev = p.ReadValue<byte>(_hrfStringPtr - i - 1);
                    // ignore this byte if this is part of a mov
                    if ((prev <= 0x8E && prev >= 0x89))
                        continue;

                    _hrfStringPtr -= i;
                    Debug.WriteLine($"host_runframe target cmp at 0x{_hrfStringPtr.ToString("X")}");
                    goto success;
                }
            }
            return false;

            success:
            // allocate memory for our detour
            IntPtr workSpace = p.AllocateMemory(0x100);
            // bytes of the ptr to our custom tick count var
            byte[] timePtrBytes = BitConverter.GetBytes(workSpace.ToInt32());
            _custTimeInjection = new Detour(
                p,
                _hrfStringPtr,
                workSpace + 0x8,
                6 + i,
                new byte[]
                {
                    // inc [tick count val]
                    0xFF, 0x05, timePtrBytes[0], timePtrBytes[1], timePtrBytes[2], timePtrBytes[3],
                },
                true);
            // because the detour just simply copies bytes over, we'll have to rewrite the jl instruction
            // to correct the offset back to the start of the loop
            // bytes of the ptr from the detoured jl to the start of the loop
            byte[] loopToBytes = BitConverter.GetBytes((int)loopTo - (int)(workSpace + 0x8 + 0x6 + i + 0x6));
            p.WriteBytes(workSpace + 0x8 + 6 + i, new byte[]
                {
                    0x0F, 0x8C, loopToBytes[0], loopToBytes[1], loopToBytes[2], loopToBytes[3],
                });
            // final memory layout should look like this:
            // workspace + 0x0          [tick count val]
            // workspace + 0x8          inc [tick count val]
            // workspace + 0xE          (cmp and along with any other bytes in between that and the jl)
            // workspace + 0xE + i      jl [loop start]
            // workspace + 0xE + i + 6  jmp [loop end]
            _custTimeCountPtr = workSpace;
            _custTimeCountWatcher = new ValueWatcher<int>(0);
            #endregion

            // get the game dir now to evaluate game-specific stuff
            GetGameDir(p, offsets);

            if (_isInfra)
                _infraIsLoading = new MemoryWatcher<byte>(new DeepPointer(scanner.Scan(_infraIsLoadingTarget), 0x0));
            #endregion

            #region SERVER
            // required server stuff
            var serverScanner = new SignatureScanner(p, server.BaseAddress, server.ModuleMemorySize);

            if (!scanForPtr(ref offsets.GlobalEntityListPtr, _globalEntityListTarget, serverScanner, "CBaseEntityList::(CEntInfo)m_EntPtrArray"))
                return false;

            if (!scanForPtr(ref offsets.EventQueuePtr, _eventQueueTarget, serverScanner, "CEventQueue::m_Events", "1")
                && !scanForPtr(ref offsets.EventQueuePtr, _eventQueueTarget2, serverScanner, "CEventQueue::m_Events", "2")
                && !scanForPtr(ref offsets.EventQueuePtr, _eventQueueTarget3, serverScanner, "CEventQueue::m_Events", "3"))
            {
                Debug.WriteLine("Event Queue ptr not found!");
            }
            #endregion

            #region CLIENT
            // optional client fade list
            ProcessModuleWow64Safe client = p.ModulesWow64Safe().FirstOrDefault(x => x.ModuleName.ToLower() == "client.dll");
            if (client != null)
            {
                var clientScanner = new SignatureScanner(p, client.BaseAddress, client.ModuleMemorySize);
                IntPtr tmpfade = clientScanner.Scan(_fadeListTarget);
                if (tmpfade == IntPtr.Zero) 
                {
                    // because of how annoyingly hard it is to traditionally sigscan this we'll have to resort to function searching
                    // find the reference to the string "%i gametitle fade\n" near which lies gViewEffects/m_FadeList
                    // subtract 12 bytes from that pointer to get past a gpGlobals reference which would bring up a 2nd result in our final sigscan
                    // subtract another 0x50 bytes from that pointer to get a new base address, then set 0x50 as the module size
                    // then sigscan

                    // support range: old engine 4104 & new engine non-portal branch between 2007 and 2013

                    IntPtr stringptr = clientScanner.Scan(new SigScanTarget(0, "25692067616D657469746C6520666164650A"));
                    byte[] b = BitConverter.GetBytes(stringptr.ToInt32());
                    var target = new SigScanTarget(-12, $"68 {b[0]:X02} {b[1]:X02} {b[2]:X02} {b[3]:X02}");

                    IntPtr endptr = clientScanner.Scan(target);
                    clientScanner = new SignatureScanner(p, endptr - 0x50, 0x50);

                    target = new SigScanTarget(2, "8B 0D ?? ?? ?? ??"); // push m_FadeList
                    target.OnFound = (proc, scanner, ptr) => !proc.ReadPointer(proc.ReadPointer(ptr), out ptr) ? IntPtr.Zero : ptr;
                    tmpfade = clientScanner.Scan(target);
                }

                offsets.FadeListPtr = tmpfade;
            }
            #endregion

            #region OFFSETS
            // entity offsets
            if ( !GetBaseEntityMemberOffset("m_fFlags", p, serverScanner, out offsets.BaseEntityFlagsOffset)
                || !GetBaseEntityMemberOffset("m_vecAbsOrigin", p, serverScanner, out offsets.BaseEntityAbsOriginOffset)
                || !GetBaseEntityMemberOffset("m_iName", p, serverScanner, out offsets.BaseEntityTargetNameOffset)
                // source 2003 leak doesn't define m_hViewEntity as a field so for the time being this is ignored
                || (!GetBaseEntityMemberOffset("m_hViewEntity", p, serverScanner, out offsets.BasePlayerViewEntity) && !IsSource2003))
                return false;

            // find m_pParent offset. the string "m_pParent" occurs more than once so we have to do something else
            // in old engine it's right before m_iParentAttachment. in new engine it's right before m_nTransmitStateOwnedCounter
            // TODO: test on all engines
            int tmp;
            if (!GetBaseEntityMemberOffset("m_nTransmitStateOwnedCounter", p, serverScanner, out tmp))
            {
                if (!GetBaseEntityMemberOffset("m_iParentAttachment", p, serverScanner, out tmp))
                    return false;
                tmp -= 4; // sizeof m_iParentAttachment
            }
            tmp -= 4; // sizeof m_nTransmitStateOwnedCounter (4 aligned byte)
            offsets.BaseEntityParentHandleOffset = tmp;

            Debug.WriteLine("CViewEffects::m_FadeList (g_ViewEffects) ptr = 0x" + offsets.FadeListPtr.ToString("X"));
            Debug.WriteLine("CBaseEntity::m_fFlags offset = 0x" + offsets.BaseEntityFlagsOffset.ToString("X"));
            Debug.WriteLine("CBaseEntity::m_vecAbsOrigin offset = 0x" + offsets.BaseEntityAbsOriginOffset.ToString("X"));
            Debug.WriteLine("CBaseEntity::m_iName offset = 0x" + offsets.BaseEntityTargetNameOffset.ToString("X"));
            Debug.WriteLine("CBaseEntity::m_pParent offset = 0x" + offsets.BaseEntityParentHandleOffset.ToString("X"));
            Debug.WriteLine("CBasePlayer::m_hViewEntity offset = 0x" + offsets.BasePlayerViewEntity.ToString("X"));
            Debug.WriteLine("CEventQueue::m_Events ptr = 0x" + offsets.EventQueuePtr.ToString("X"));
            #endregion

#if DEBUG
            Debug.WriteLine("TryGetGameProcess took: " + sw.Elapsed);
#endif

            return true;
        }

        // also works for anything derived from CBaseEntity (player etc) (no multiple inheritance)
        // var must be included by one of the DEFINE_FIELD macros
        public static bool GetBaseEntityMemberOffset(string member, Process game, SignatureScanner scanner, out int offset)
        {
            offset = -1;

            IntPtr stringPtr = scanner.Scan(new SigScanTarget(0, Encoding.ASCII.GetBytes(member)));

            if (stringPtr == IntPtr.Zero)
                return false;

            var target = new SigScanTarget(10,
                $"C7 05 ?? ?? ?? ?? {stringPtr.GetByteString()}"); // mov     dword_15E2BF1C, offset aM_fflags ; "m_fFlags"
            target.OnFound = (proc, s, ptr) => {
                // this instruction is almost always directly after above one, but there are a few cases where it isn't
                // so we have to scan down and find it
                var proximityScanner = new SignatureScanner(proc, ptr, 256);
                return proximityScanner.Scan(new SigScanTarget(6, "C7 05 ?? ?? ?? ?? ?? ?? 00 00"));         // mov     dword_15E2BF20, 0CCh
            };

            IntPtr addr = scanner.Scan(target);
            if (addr == IntPtr.Zero)
            {
                // seen in Black Mesa Source (legacy version)
                var target2 = new SigScanTarget(1,
                 "68 ?? ?? ?? ??",                                  // push    256
                $"68 {stringPtr.GetByteString()}"); // push    offset aM_fflags ; "m_fFlags"
                addr = scanner.Scan(target2);

                if (addr == IntPtr.Zero)
                    return false;
            }

            return game.ReadValue(addr, out offset);
        }

        void HandleProcess(Process game, GameOffsets offsets, CancellationTokenSource cts)
        {
            Debug.WriteLine("HandleProcess " + game.ProcessName);

            var state = new GameState(game, offsets);
            _state = state;
            this.InitGameState(state);
            _gotTickRate = false;

            bool forceExit = false;

            CancellationTokenSource cancelForceExit = new CancellationTokenSource();
            Thread checkGameProcess = new Thread( new ThreadStart(() => 
            {
                // REALLY make sure the game has exited, as sometimes the splitter stops functioning
                // after game crash for some people
                while (!cancelForceExit.IsCancellationRequested)
                {
                    forceExit = !Process.GetProcesses().Any(x => x.ProcessName == game.ProcessName);
                    if (forceExit && !game.HasExited)
                        Debug.WriteLine("HasExited was wrong!!!!");
                    Thread.Sleep(7500);
                }
            }));

            checkGameProcess.Start();

            var profiler = Stopwatch.StartNew();
            while (!game.HasExited && !forceExit && !cts.IsCancellationRequested)
            {
                // iteration must never take longer than 1 tick
                var temp = state.GameSupport;
                this.UpdateGameState(state);
                state.GameSupport?.OnGenericUpdateFull(state);
                this.CheckGameState(state);
                state.GameSupport = temp;

                state.UpdateCount++;
                TimedTraceListener.Instance.UpdateCount = state.UpdateCount;

                if (profiler.ElapsedMilliseconds >= TARGET_UPDATE_RATE)
                {
                    _timesOver += 1;
                    _timeOverSpent += Convert.ToInt32(profiler.ElapsedMilliseconds) - TARGET_UPDATE_RATE;
                    Debug.WriteLine("**** update iteration took too long: " + profiler.ElapsedMilliseconds + "ms, times: " + _timesOver + ", total: " + _timeOverSpent + "ms");
                }

                //var sleep = Stopwatch.StartNew();
                //MapTimesForm.Instance.Text = profiler.Elapsed.ToString();
                Thread.Sleep(Math.Max(TARGET_UPDATE_RATE - (int)profiler.ElapsedMilliseconds, 1));
                //MapTimesForm.Instance.Text = sleep.Elapsed.ToString();
                profiler.Restart();
            }

            cancelForceExit.Cancel();
            forceExit = false;

            // if the game crashed, make sure session ends
            if (state.HostState.Current == HostState.Run)
                this.SendSessionEndedEvent();
        }

        void InitGameState(GameState state)
        {
            // special case for half-life 2 survivor, scan the subdirectories and find specifically-named folder.
            string[] hl2SurvivorDirs = { "hl2mp_japanese", "hl2_japanese" };
            string dir = Path.GetDirectoryName(state.GameProcess.MainModule.FileName);
            var subdir = new DirectoryInfo(dir).GetDirectories();

            if (dir != null && subdir.Any(di => hl2SurvivorDirs.Contains(di.Name.ToLower())))
                state.GameDir = "survivor";
            else state.GameDir = new DirectoryInfo(GetGameDir(state.GameProcess, state.GameOffsets)).Name.ToLower();
            Debug.WriteLine("gameDir = " + state.GameDir);

            state.CurrentMap = String.Empty;

            // inspect memory layout to determine CEntInfo's version
            const int SERIAL_MASK = 0x7FFF;
            int serial;
            state.GameProcess.ReadValue(state.GameOffsets.GlobalEntityListPtr + (4 * 7), out serial);
            state.GameOffsets.EntInfoSize = (!IsSource2003) ? ((serial > 0 && serial < SERIAL_MASK) ? CEntInfoSize.Portal2 : CEntInfoSize.HL2) : CEntInfoSize.Source2003;

            state.GameSupport = GameSupport.FromGameDir(state.GameDir);

            if (state.GameSupport != null)
            {
                Debug.WriteLine("running game-specific code for: " + state.GameDir);
                state.GameSupport.OnGameAttachedFull(state);
            }

            this.SendSetTimingMethodEvent(state.GameSupport?.GameTimingMethod ?? GameTimingMethod.EngineTicks);
        }

        HostState GetHostState(Process game, GameOffsets offsets)
        {
            game.ReadValue(offsets.HostStatePtr, out int hostState);

            // in infra, hoststates above 1 are offset by 1
            if (_isInfra)
                return (HostState)((hostState > 1) ? hostState - 1 : hostState);
            else
                return (HostState)hostState;
        }

        private SignOnState GetSignOnState(Process game, GameOffsets offsets)
        {
            game.ReadValue(offsets.SignOnStatePtr, out int signOnState);

            // infra's signonstate is unreliable because it isn't updated on the "load" command and some others
            // so we'll have to settle with a loading byte
            if (_isInfra)
            {
                _infraIsLoading.Update(game);

                switch (_infraIsLoading.Current)
                {
                    case 0:
                        return SignOnState.Full;

                    case 1:
                        return SignOnState.None;

                    default:
                        return (SignOnState)signOnState;
                }
            }
            // source 2003 leak has a completely different signonstate structure with only 5 entries
            else if (IsSource2003) 
            {
                if (signOnState <= 1)
                    return SignOnState.None;
                else
                {
                    if (signOnState == 4)
                        return SignOnState.Full;
                    return SignOnState.Connected;
                }
            }
            else return (SignOnState)signOnState;
        }

        private ServerState GetServerState(Process game, GameOffsets offsets)
        {
            game.ReadValue(offsets.ServerStatePtr, out int serverState);

            if (IsSource2003)
            {
                // this is actually how the game knows if it's paused or not..., source 2003 leak's serverstate enum doesn't have
                // paused as an entry for some reason
                game.ReadValue(offsets.CurTimePtr + 0x4, out float curFrameTime);
                if (curFrameTime == 0f)
                    return ServerState.Paused;
                return (ServerState)serverState;
            }
            else return (ServerState)(serverState);
        }

        void UpdateGameState(GameState state)
        {
            Process game = state.GameProcess;
            GameOffsets offsets = state.GameOffsets;

            // update all the stuff that doesn't depend on the signon state
            state.RawTickCount.Current = game.ReadValue<int>(offsets.TickCountPtr);
            game.ReadValue(offsets.CurTimePtr + 0x4, out state.FrameTime);
            game.ReadValue(offsets.IntervalPerTickPtr, out state.IntervalPerTick);

            _custTimeCountWatcher.Current = state.GameProcess.ReadValue<int>(_custTimeCountPtr);

            state.SignOnState.Current = GetSignOnState(game, offsets);
            state.HostState.Current = GetHostState(game, offsets);
            state.ServerState.Current = GetServerState(game, offsets);

            bool firstTick = false;

            // update the stuff that's only valid during signon state full
            if (state.SignOnState.Current == SignOnState.Full)
            {
                // if signon state just became full (where demos start timing from)
                if (state.SignOnState.Current != state.SignOnState.Old)
                {
                    firstTick = true;

                    // start rebasing from this tick
                    state.TickBase = state.RawTickCount.Current;
                    Debug.WriteLine("rebasing ticks from " + state.TickBase);

                    // player was just spawned, get it's ptr
                    state.PlayerEntInfo = state.GetEntInfoByIndex(GameState.ENT_INDEX_PLAYER);

                    // update map name
                    state.GameProcess.ReadString(state.GameOffsets.CurMapPtr, ReadStringType.ASCII, 64, out state.CurrentMap);
                }
                if (state.RawTickCount.Current - state.TickBase < 0)
                {
                    Debug.WriteLine("based ticks is wrong by " + (state.RawTickCount.Current - state.TickBase) + " rebasing from " + state.TickBase);
                    state.TickBase = state.RawTickCount.Current;
                }

                // update time and rebase it against the first signon state full tick
                if (state.RawTickCount.Current < state.RawTickCount.Old)
                    Debug.WriteLine($"tick count undershot by {state.RawTickCount.Current - state.RawTickCount.Old}");
                state.TickCount = state.RawTickCount.Current - state.TickBase;
                state.TickTime = state.TickCount * state.IntervalPerTick;
                TimedTraceListener.Instance.TickCount = state.TickCount;

                // update player related things
                if (state.PlayerEntInfo.EntityPtr != IntPtr.Zero && state.GameSupport != null)
                {
                    // flags
                    if (state.GameSupport.RequiredProperties.HasFlag(PlayerProperties.Flags))
                        state.PlayerFlags.Current = game.ReadValue<FL>(state.PlayerEntInfo.EntityPtr + offsets.BaseEntityFlagsOffset);

                    // position
                    if (state.GameSupport.RequiredProperties.HasFlag(PlayerProperties.Position))
                        state.PlayerPosition.Current = game.ReadValue<Vector3f>(state.PlayerEntInfo.EntityPtr + offsets.BaseEntityAbsOriginOffset);

                    // view entity
                    if (state.GameSupport.RequiredProperties.HasFlag(PlayerProperties.ViewEntity))
                    {
                        const int ENT_ENTRY_MASK = 0x7FF;

                        int viewEntityHandle; // EHANDLE
                        game.ReadValue(state.PlayerEntInfo.EntityPtr + offsets.BasePlayerViewEntity, out viewEntityHandle);
                        state.PlayerViewEntityIndex.Current = viewEntityHandle == -1
                            ? GameState.ENT_INDEX_PLAYER
                            : viewEntityHandle & ENT_ENTRY_MASK;
                    }

                    // parent entity
                    if (state.GameSupport.RequiredProperties.HasFlag(PlayerProperties.ParentEntity))
                        state.PlayerParentEntityHandle.Current = game.ReadValue<int>(state.PlayerEntInfo.EntityPtr + offsets.BaseEntityParentHandleOffset);

                    // if it's the first tick, don't use stuff from the previous map
                    if (firstTick)
                    {
                        state.PlayerFlags.Current = state.PlayerFlags.Old;
                        state.PlayerPosition.Current = state.PlayerPosition.Old;
                        state.PlayerViewEntityIndex.Current = state.PlayerViewEntityIndex.Old;
                        state.PlayerParentEntityHandle.Current = state.PlayerParentEntityHandle.Old;
                    }
                }
            } // if (state.SignOnState.Current == SignOnState.Full)
        }

        void CheckGameState(GameState state)
        {
            if (state.IntervalPerTick > 0 && !_gotTickRate)
            {
                _gotTickRate = true;
                this.SendSetTickRateEvent(state.IntervalPerTick);
            }

            if (state.SignOnState.Current != state.SignOnState.Old)
                Debug.WriteLine("SignOnState changed to " + state.SignOnState.Current);


            // if player is fully in game
            if (state.SignOnState.Current == SignOnState.Full && state.HostState.Current == HostState.Run)
            {
                // note: seems to be slow sometimes. ~3ms
                
                if (state.TickCount > 0)
                {
                    if (state.ServerState.Current == ServerState.Paused)
                        this.SendMiscTimeEvent(_custTimeCountWatcher.Current - _custTimeCountWatcher.Old, MiscTimeType.PauseTime);
                    else this.SendSessionTimeUpdateEvent(_custTimeCountWatcher.Current - _custTimeCountWatcher.Old);
                }

                // first tick when player is fully in game
                if (state.SignOnState.Current != state.SignOnState.Old)
                {
                    Debug.WriteLine("session started");
                    this.SendSessionStartedEvent(state.CurrentMap);

                    state.GameSupport?.OnSessionStartFull(state);
                }

                if (state.ServerState.ChangedTo(ServerState.Paused))
                    this.SendMiscTimeEvent(0, MiscTimeType.StartPause);
                if (state.ServerState.ChangedFrom(ServerState.Paused))
                    this.SendMiscTimeEvent(0, MiscTimeType.EndPause);

                if (state.GameSupport != null)
                    this.HandleGameSupportResult(state.GameSupport.OnUpdateFull(state), state);

#if DEBUG
                if (state.PlayerEntInfo.EntityPtr != IntPtr.Zero)
                    DebugPlayerState(state);
#endif
            }

            if (state.HostState.Current != state.HostState.Old)
            {
                if (state.HostState.Old == HostState.Run)
                {
                    // the map changed or a quicksave was loaded
                    Debug.WriteLine("session ended");

                    state.TickBase = -1;

                    // the map changed or a save was loaded
                    this.SendSessionEndedEvent();

                    if (state.GameSupport != null && state.HostState.Current == HostState.GameShutdown)
                    {
                        if (state.QueueOnNextSessionEnd == GameSupportResult.PlayerLostControl)
                            this.HandleGameSupportResult(GameSupportResult.PlayerLostControl, state);

                        // note: logically map name should only have been set when host state is NewGame, 
                        // however the map and disconnect commands queue host state changes and host disconnect is always called first
                        // always leaving a window of time between map name changing and host state changing to newgame
                        else if (state.QueueOnNextSessionEnd == GameSupportResult.ManualSplit)
                        {
                            string levelName;
                            state.GameProcess.ReadString(state.GameOffsets.HostStateLevelNamePtr, ReadStringType.ASCII, 256 - 1, out levelName);
                            this.SendMapChangedEvent(levelName, state.CurrentMap);
                        }
                    }
                        
                    state.GameSupport?.OnSessionEndFull(state);
                }

                Debug.WriteLine("host state changed to " + state.HostState.Current);

                // HostState::m_levelName is changed much earlier than state.CurrentMap (CBaseServer::m_szMapName)
                // reading HostStateLevelNamePtr is only valid during these states (not LoadGame!)
                if (state.HostState.Current == HostState.ChangeLevelSP || state.HostState.Current == HostState.ChangeLevelMP
                    || state.HostState.Current == HostState.NewGame)
                {
                    string levelName;
                    state.GameProcess.ReadString(state.GameOffsets.HostStateLevelNamePtr, ReadStringType.ASCII, 256 - 1, out levelName);
                    Debug.WriteLine("host state m_levelName changed to " + levelName);

                    // only for the beginner's guide, the "restart the level" option does a changelevel back to the currentmap rather than
                    // simply doing "restart"
                    // if the runner uses this option to reset at the first map then restart the timer
                    if (state.HostState.Current == HostState.NewGame || state.GameDir.ToLower() == "beginnersguide")
                    {
                        if (state.GameSupport != null)
                        {
                            if (state.GameSupport.FirstMap.Contains(levelName))
                                this.SendNewGameStartedEvent(levelName);

                            if (!string.IsNullOrWhiteSpace(levelName) && 
                                (levelName == _settings.StartMap
                                || state.GameSupport.StartOnFirstLoadMaps.Contains(levelName)
                                || state.GameSupport.AdditionalGameSupport.Any(x => x.StartOnFirstLoadMaps.Contains(levelName))))
                            {
                                // do a debug spew of the timer start
                                Debug.WriteLine(state.GameDir + " start on " + levelName);
                                this.HandleGameSupportResult(GameSupportResult.PlayerGainedControl, state);
                            }
                        }
                    }
                    else // changelevel sp/mp
                    {
                        // state.CurrentMap should still be the previous map
                        this.SendMapChangedEvent(levelName, state.CurrentMap);
                    }
                }

                if (state.GameSupport != null)
                    state.QueueOnNextSessionEnd = GameSupportResult.DoNothing;
            }


            if (state.ServerState.Current == ServerState.Dead && state.HostState.Current == HostState.Run)
                this.SendMiscTimeEvent(_custTimeCountWatcher.Current - _custTimeCountWatcher.Old, MiscTimeType.ClientDisconnectTime);
        }

        void HandleGameSupportResult(GameSupportResult result, GameState state)
        {
            if (result == GameSupportResult.DoNothing)
                return;

            switch (result)
            {
                case GameSupportResult.PlayerGainedControl:
                    this.SendGainedControlEvent(state.GameSupport.StartOffsetTicks);
                    break;
                case GameSupportResult.PlayerLostControl:
                    this.SendLostControlEvent(state.GameSupport.EndOffsetTicks);
                    break;
                case GameSupportResult.ManualSplit:
                    this.SendManualSplit(state.GameSupport.EndOffsetTicks);
                    break;
            }
        }

        #region UI THREAD FUNCTIONS
        // these functions are ugly but it means we don't have to worry about implicitly captured closures
        public class MapChangedEventArgs : EventArgs
        {
            public string Map { get; private set; }
            public string PrevMap { get; private set; }
            public MapChangedEventArgs(string map, string prevMap)
            {
                this.Map = map;
                this.PrevMap = prevMap;
            }
        }
        public void SendMapChangedEvent(string mapName, string prevMapName)
        {
            _uiThread.Post(d => {
                this.OnMapChanged?.Invoke(this, new MapChangedEventArgs(mapName, prevMapName));
            }, null);
        }

        public class SessionTicksUpdateEventArgs : EventArgs
        {
            public int TickDifference { get; private set; }
            public SessionTicksUpdateEventArgs(int tickDifference)
            {
                this.TickDifference = tickDifference;
            }
        }
        public void SendSessionTimeUpdateEvent(int tickDifference)
        {
            // note: sometimes this takes a few ms
            _uiThread.Post(d => {
                this.OnSessionTimeUpdate?.Invoke(this, new SessionTicksUpdateEventArgs(tickDifference));
            }, null);
        }

        public class PlayerControlChangedEventArgs : EventArgs
        {
            public int TicksOffset { get; private set; }
            public PlayerControlChangedEventArgs(int ticksOffset)
            {
                this.TicksOffset = ticksOffset;
            }
        }
        public void SendGainedControlEvent(int ticksOffset)
        {
            _uiThread.Post(d => {
                this.OnPlayerGainedControl?.Invoke(this, new PlayerControlChangedEventArgs(ticksOffset));
            }, null);
        }

        public void SendLostControlEvent(int ticksOffset)
        {
            _uiThread.Post(d => {
                this.OnPlayerLostControl?.Invoke(this, new PlayerControlChangedEventArgs(ticksOffset));
            }, null);
        }

        public void SendManualSplit(int ticksOffset)
        {
            _uiThread.Post(d => {
                this.ManualSplit?.Invoke(this, new PlayerControlChangedEventArgs(ticksOffset));
            }, null);
        }

        public class SessionStartedEventArgs : EventArgs
        {
            public string Map { get; private set; }
            public SessionStartedEventArgs(string map)
            {
                this.Map = map;
            }
        }
        public void SendSessionStartedEvent(string map)
        {
            _uiThread.Post(d => {
                this.OnSessionStarted?.Invoke(this, new SessionStartedEventArgs(map));
            }, null);
        }

        public void SendSessionEndedEvent()
        {
            _uiThread.Post(d => {
                this.OnSessionEnded?.Invoke(this, EventArgs.Empty);
            }, null);
        }

        public void SendNewGameStartedEvent(string map)
        {
            _uiThread.Post(d => {
                this.OnNewGameStarted?.Invoke(this, EventArgs.Empty);
            }, null);
        }

        public class MiscTimeEventArgs : EventArgs
        {
            public int TickDifference { get; private set; }
            public MiscTimeType Type { get; private set; }
            public MiscTimeEventArgs(int tickDiff, MiscTimeType type)
            {
                this.TickDifference = tickDiff;
                this.Type = type;
            }
        }
        public enum MiscTimeType
        {
            StartPause,
            EndPause,
            PauseTime,
            ClientDisconnectTime
        }
        public void SendMiscTimeEvent(int tickDiff, MiscTimeType type)
        {
            _uiThread.Post(d => {
                this.OnMiscTime?.Invoke(this, new MiscTimeEventArgs(tickDiff, type));
            }, null);
        }
        public class SetTickRateEventArgs : EventArgs
        {
            public float IntervalPerTick { get; private set; }
            public SetTickRateEventArgs(float intervalPerTick)
            {
                this.IntervalPerTick = intervalPerTick;
            }
        }
        public void SendSetTickRateEvent(float intervalPerTick)
        {
            _uiThread.Post(d => {
                this.OnSetTickRate?.Invoke(this, new SetTickRateEventArgs(intervalPerTick));
            }, null);
        }
        

        public class SetTimingMethodEventArgs : EventArgs
        {
            public GameTimingMethod GameTimingMethod { get; private set; }
            public SetTimingMethodEventArgs(GameTimingMethod gameTimingMethod)
            {
                this.GameTimingMethod = gameTimingMethod;
            }
        }
        public void SendSetTimingMethodEvent(GameTimingMethod gameTimingMethod)
        {
            _uiThread.Post(d => {
                this.OnSetTimingMethod?.Invoke(this, new SetTimingMethodEventArgs(gameTimingMethod));
            }, null);
        }
        #endregion

#if DEBUG
        void DebugPlayerState(GameState state)
        {
            if (state.PlayerFlags.Changed)
            {
                string addedList = String.Empty;
                string removedList = String.Empty;
                foreach (FL flag in Enum.GetValues(typeof(FL)))
                {
                    if (state.PlayerFlags.Current.HasFlag(flag) && !state.PlayerFlags.Old.HasFlag(flag))
                        addedList += Enum.GetName(typeof(FL), flag) + " ";
                    else if (!state.PlayerFlags.Current.HasFlag(flag) && state.PlayerFlags.Old.HasFlag(flag))
                        removedList += Enum.GetName(typeof(FL), flag) + " ";
                }
                if (addedList.Length > 0)
                    Debug.WriteLine("player flags added: " + addedList);
                if (removedList.Length > 0)
                    Debug.WriteLine("player flags removed: " + removedList);
            }

            if (state.PlayerViewEntityIndex.Current != state.PlayerViewEntityIndex.Old)
            {
                Debug.WriteLine("player view entity changed: " + state.PlayerViewEntityIndex.Current);
            }

            if (state.PlayerParentEntityHandle.Current != state.PlayerParentEntityHandle.Old)
            {
                Debug.WriteLine("player parent entity changed: " + state.PlayerParentEntityHandle.Current.ToString("X"));
            }

#if false
            if (!state.PlayerPosition.Current.BitEquals(state.PlayerPosition.Old))
            {
                Debug.WriteLine("player pos changed: " + state.PlayerParentEntityHandle.Current);
            }
#endif
        }
#endif
    }

    public enum GameTimingMethod
    {
        EngineTicks,
        EngineTicksWithPauses,
        AllEngineTicks
        //RealTimeWithoutLoads
    }

    enum SignOnState
    {
        None = 0,
        Challenge = 1,
        Connected = 2,
        New = 3,
        PreSpawn = 4,
        Spawn = 5,
        Full = 6,
        ChangeLevel = 7
    }

    // HOSTSTATES
    enum HostState
    {
        NewGame = 0,
        LoadGame = 1,
        ChangeLevelSP = 2,
        ChangeLevelMP = 3,
        Run = 4,
        GameShutdown = 5,
        Shutdown = 6,
        Restart = 7
    }

    // server_state_t
    enum ServerState
    {
        Dead,
        Loading,
        Active,
        Paused
    }

    // MapLoadType_t
    enum MapLoadType
    {
        NewGame = 0,
        LoadGame = 1,
        Transition = 2,
        Background = 3
    }

    enum CEntInfoSize
    {
        Source2003 = 4 * 3,
        HL2 = 4 * 4,
        Portal2 = 4 * 6
    }

    [Flags]
    public enum FL
    {
        //ONGROUND = (1<<0),
        //DUCKING = (1<<1),
        //WATERJUMP = (1<<2),
        ONTRAIN = (1 << 3),
        INRAIN = (1 << 4),
        FROZEN = (1 << 5),
        ATCONTROLS = (1 << 6),
        CLIENT = (1 << 7),
        FAKECLIENT = (1 << 8),
        //INWATER = (1<<9),
        FLY = (1 << 10),
        SWIM = (1 << 11),
        CONVEYOR = (1 << 12),
        NPC = (1 << 13),
        GODMODE = (1 << 14),
        NOTARGET = (1 << 15),
        AIMTARGET = (1 << 16),
        PARTIALGROUND = (1 << 17),
        STATICPROP = (1 << 18),
        GRAPHED = (1 << 19),
        GRENADE = (1 << 20),
        STEPMOVEMENT = (1 << 21),
        DONTTOUCH = (1 << 22),
        BASEVELOCITY = (1 << 23),
        WORLDBRUSH = (1 << 24),
        OBJECT = (1 << 25),
        KILLME = (1 << 26),
        ONFIRE = (1 << 27),
        DISSOLVING = (1 << 28),
        TRANSRAGDOLL = (1 << 29),
        UNBLOCKABLE_BY_PLAYER = (1 << 30)
    }
}
