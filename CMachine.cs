using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace PedalPatch
{
    public class CMachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) { return new GUI(); }
    }

    [MachineDecl(Name = "Pedal Patcher", ShortName = "Pedal Patch", Author = "Managed",
                 MaxTracks = 16, InputCount = CMachine.NumInputs, OutputCount = CMachine.NumOutputs)]
    public class CMachine : IBuzzMachine, INotifyPropertyChanged
    {
        readonly IBuzzMachineHost host;

        public const int NumInputs  = 6;
        public const int NumOutputs = 6;
        public const int NumPatches = 48;

        readonly bool[,,] routing    = new bool[NumPatches, NumInputs, NumOutputs];
        readonly float[,] gain       = new float[NumInputs, NumOutputs];
        readonly float[,] targetGain = new float[NumInputs, NumOutputs];
        readonly string[] inputLabels  = new string[NumInputs];
        readonly string[] outputLabels = new string[NumOutputs];

        bool[,] clipboard;
        CMachineStateData pendingLoad;
        int _currentPatch;

        // Cached reflection helpers
        System.Reflection.PropertyInfo _bufferProp;
        System.Reflection.PropertyInfo _destChProp;
        System.Reflection.PropertyInfo _srcChProp;

        public event PropertyChangedEventHandler PropertyChanged;
        void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        // ── Construction ──────────────────────────────────────────────────────
        public CMachine(IBuzzMachineHost host)
        {
            this.host = host;
            for (int i = 0; i < NumInputs;  i++) inputLabels[i]  = $"In {i + 1}";
            for (int o = 0; o < NumOutputs; o++) outputLabels[o] = $"Out {o + 1}";

            // host.Machine is null at construction time; defer one dispatcher tick
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var f = System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance;
                    host.Machine?.GetType().GetProperty("InputChannelCount",  f)?.SetValue(host.Machine, NumInputs);
                    host.Machine?.GetType().GetProperty("OutputChannelCount", f)?.SetValue(host.Machine, NumOutputs);
                }
                catch { }
            }));
        }

        // ── Global parameter ──────────────────────────────────────────────────
        [ParameterDecl(IsStateless = false, Name = "Patch",
                       MinValue = 0, MaxValue = NumPatches - 1, DefValue = 0,
                       Description = "Active patch (0-23)")]
        public int CurrentPatch
        {
            get => _currentPatch;
            set
            {
                if (_currentPatch == value) return;
                _currentPatch = value;
                RefreshTargetGains();
                Notify(nameof(CurrentPatch));
                Notify(nameof(Routing));
            }
        }

        // ── Settings ──────────────────────────────────────────────────────────
        int _fadeTimeMs = 10;
        public int FadeTimeMs
        {
            get => _fadeTimeMs;
            set { _fadeTimeMs = Math.Clamp(value, 0, 500); Notify(nameof(FadeTimeMs)); }
        }

        bool _confirmOnClear = true;
        public bool ConfirmOnClear
        {
            get => _confirmOnClear;
            set { _confirmOnClear = value; Notify(nameof(ConfirmOnClear)); }
        }

        // ── Track parameters ──────────────────────────────────────────────────
        public class TrackState
        {
            [ParameterDecl(IsStateless = true, Name = "Command",
                           MinValue = 0, MaxValue = 8, DefValue = 255,
                           Description = "0=Unplug 1=Plug 2=PlugEx 3=InAll 4=OutAll 5=DisIn 6=DisOut 7=All 8=Clear")]
            public byte Command { get; set; }

            [ParameterDecl(IsStateless = true, Name = "Argument",
                           MinValue = 0, MaxValue = 0xFFFF, DefValue = 0xFFFF,
                           Description = "High byte = input (0-based), Low byte = output (0-based)")]
            public int Argument { get; set; }
        }

        public TrackState[] Tracks { get; set; }

        // ── Routing accessors ─────────────────────────────────────────────────
        public bool[,,] Routing      => routing;
        public string[] InputLabels  => inputLabels;
        public string[] OutputLabels => outputLabels;

        public bool GetConnection(int input, int output) => routing[_currentPatch, input, output];

        public void SetConnection(int input, int output, bool value)
        {
            routing[_currentPatch, input, output] = value;
            targetGain[input, output] = value ? 1f : 0f;
            Notify(nameof(Routing));
        }

        public void SetInputLabel(int input, string label)
        {
            if (input < 0 || input >= NumInputs) return;
            inputLabels[input] = label ?? $"In {input + 1}";
            Notify(nameof(InputLabels));
        }

        public void SetOutputLabel(int output, string label)
        {
            if (output < 0 || output >= NumOutputs) return;
            outputLabels[output] = label ?? $"Out {output + 1}";
            Notify(nameof(OutputLabels));
        }

        // ── Patch operations ──────────────────────────────────────────────────
        public void ClearCurrentPatch()
        {
            for (int i = 0; i < NumInputs;  i++)
            for (int o = 0; o < NumOutputs; o++)
                routing[_currentPatch, i, o] = false;
            RefreshTargetGains();
            Notify(nameof(Routing));
        }

        public void ClearAllPatches()
        {
            Array.Clear(routing, 0, routing.Length);
            RefreshTargetGains();
            Notify(nameof(Routing));
        }

        public void CopyCurrentPatch()
        {
            clipboard = new bool[NumInputs, NumOutputs];
            for (int i = 0; i < NumInputs;  i++)
            for (int o = 0; o < NumOutputs; o++)
                clipboard[i, o] = routing[_currentPatch, i, o];
            Notify(nameof(HasClipboard));
        }

        public void PasteCurrentPatch(bool merge = false)
        {
            if (clipboard == null) return;
            for (int i = 0; i < NumInputs;  i++)
            for (int o = 0; o < NumOutputs; o++)
                routing[_currentPatch, i, o] = merge
                    ? routing[_currentPatch, i, o] | clipboard[i, o]
                    : clipboard[i, o];
            if (!PreserveClipboard) { clipboard = null; Notify(nameof(HasClipboard)); }
            RefreshTargetGains();
            Notify(nameof(Routing));
        }

        bool _preserveClipboard;
        public bool PreserveClipboard
        {
            get => _preserveClipboard;
            set { _preserveClipboard = value; Notify(nameof(PreserveClipboard)); }
        }

        public bool HasClipboard => clipboard != null;

        void RefreshTargetGains()
        {
            for (int i = 0; i < NumInputs;  i++)
            for (int o = 0; o < NumOutputs; o++)
                targetGain[i, o] = routing[_currentPatch, i, o] ? 1f : 0f;
        }

        // ── Audio ─────────────────────────────────────────────────────────────
        // Buffer is on the concrete ReBuzz type, not IMachineConnection interface
        Sample[] GetBuffer(IMachineConnection conn)
        {
            if (_bufferProp == null)
                _bufferProp = conn.GetType().GetProperty("Buffer");
            return _bufferProp?.GetValue(conn) as Sample[];
        }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (Tracks != null)
                foreach (var t in Tracks)
                    if (t.Command != 255) RunCommand(t.Command, t.Argument);

            if (host.Machine == null) return false;

            var inputs  = host.Machine.Inputs;
            var outputs = host.Machine.Outputs;

            // Zero all output connection buffers
            foreach (var outConn in outputs)
            {
                var buf = GetBuffer(outConn);
                if (buf == null) continue;
                for (int s = 0; s < n; s++) { buf[s].L = 0f; buf[s].R = 0f; }
                outConn.Amp = 0;
            }

            float sr    = host.MasterInfo.SamplesPerSec;
            float fadeN = _fadeTimeMs > 0 ? _fadeTimeMs * sr / 1000f : 1f;
            float step  = 1f / fadeN;
            bool anyActive = false;

            foreach (var inConn in inputs)
            {
                int inCh   = inConn.DestinationChannel;
                var inBuf  = GetBuffer(inConn);
                if (inCh < 0 || inCh >= NumInputs || inBuf == null) continue;

                foreach (var outConn in outputs)
                {
                    int outCh  = outConn.SourceChannel;
                    var outBuf = GetBuffer(outConn);
                    if (outCh < 0 || outCh >= NumOutputs || outBuf == null) continue;

                    float g  = gain[inCh, outCh];
                    float tg = targetGain[inCh, outCh];
                    if (g == 0f && tg == 0f) continue;

                    for (int s = 0; s < n; s++)
                    {
                        if      (g < tg) g = MathF.Min(g + step, tg);
                        else if (g > tg) g = MathF.Max(g - step, tg);
                        outBuf[s].L += inBuf[s].L * g;
                        outBuf[s].R += inBuf[s].R * g;
                    }
                    gain[inCh, outCh] = g;
                    outConn.Amp = 16384;
                    anyActive = true;
                }
            }

            // Write mix of active outputs to the Work output buffer
            if (output != null)
            {
                for (int s = 0; s < n; s++) { output[s].L = 0f; output[s].R = 0f; }
                foreach (var outConn in outputs)
                {
                    var mixBuf = GetBuffer(outConn);
                    if (outConn.Amp == 0 || mixBuf == null) continue;
                    for (int s = 0; s < n; s++)
                    {
                        output[s].L += mixBuf[s].L;
                        output[s].R += mixBuf[s].R;
                    }
                }
            }

            return anyActive;
        }

        // ── Track commands ────────────────────────────────────────────────────
        void RunCommand(int cmd, int arg)
        {
            int ii = (arg >> 8) & 0xFF;
            int oo =  arg       & 0xFF;
            switch (cmd)
            {
                case 0: if (ii < NumInputs && oo < NumOutputs) SetConnection(ii, oo, false); break;
                case 1: if (ii < NumInputs && oo < NumOutputs) SetConnection(ii, oo, true);  break;
                case 2:
                    if (ii < NumInputs && oo < NumOutputs)
                    {
                        for (int i = 0; i < NumInputs; i++) routing[_currentPatch, i, oo] = false;
                        SetConnection(ii, oo, true);
                    }
                    break;
                case 3: if (ii < NumInputs)  for (int o = 0; o < NumOutputs; o++) SetConnection(ii, o, true);  break;
                case 4: if (oo < NumOutputs) for (int i = 0; i < NumInputs;  i++) SetConnection(i, oo, true);  break;
                case 5: if (ii < NumInputs)  for (int o = 0; o < NumOutputs; o++) SetConnection(ii, o, false); break;
                case 6: if (oo < NumOutputs) for (int i = 0; i < NumInputs;  i++) SetConnection(i, oo, false); break;
                case 7:
                    for (int i = 0; i < NumInputs;  i++)
                    for (int o = 0; o < NumOutputs; o++)
                        SetConnection(i, o, true);
                    break;
                case 8: ClearCurrentPatch(); break;
            }
        }

        // ── Serialisation ─────────────────────────────────────────────────────
        public class CMachineStateData
        {
            public const byte CurrentVersion = 2;
            public byte Version = CurrentVersion;
            public byte[] Data;
        }

        public CMachineStateData MachineState
        {
            get
            {
                using var ms = new MemoryStream();
                using var w  = new BinaryWriter(ms);
                w.Write(CMachineStateData.CurrentVersion);
                w.Write(_fadeTimeMs);
                w.Write(_confirmOnClear);
                for (int i = 0; i < NumInputs;  i++) w.Write(inputLabels[i]  ?? "");
                for (int o = 0; o < NumOutputs; o++) w.Write(outputLabels[o] ?? "");
                for (int p = 0; p < NumPatches; p++)
                for (int i = 0; i < NumInputs;  i++)
                for (int o = 0; o < NumOutputs; o++)
                    w.Write(routing[p, i, o]);

                // Connection channel assignments keyed by connected machine name
                var inConns  = host.Machine?.Inputs  ?? (IList<IMachineConnection>)new List<IMachineConnection>();
                var outConns = host.Machine?.Outputs ?? (IList<IMachineConnection>)new List<IMachineConnection>();
                w.Write(inConns.Count);
                foreach (var c in inConns)  { w.Write(c.Source?.Name      ?? ""); w.Write(c.DestinationChannel); }
                w.Write(outConns.Count);
                foreach (var c in outConns) { w.Write(c.Destination?.Name ?? ""); w.Write(c.SourceChannel); }

                return new CMachineStateData { Data = ms.ToArray() };
            }
            set { pendingLoad = value; }
        }

        public void ImportFinished(IDictionary<string, string> machineNameMap)
        {
            if (pendingLoad?.Data == null) return;
            try
            {
                using var ms = new MemoryStream(pendingLoad.Data);
                using var r  = new BinaryReader(ms);
                byte version = r.ReadByte();
                _fadeTimeMs     = r.ReadInt32();
                _confirmOnClear = r.ReadBoolean();
                if (version == 1) r.ReadBoolean(); // _autoName was removed in v2 — discard
                for (int i = 0; i < NumInputs;  i++) inputLabels[i]  = r.ReadString();
                for (int o = 0; o < NumOutputs; o++) outputLabels[o] = r.ReadString();
                for (int p = 0; p < NumPatches; p++)
                for (int i = 0; i < NumInputs;  i++)
                for (int o = 0; o < NumOutputs; o++)
                    routing[p, i, o] = r.ReadBoolean();

                if (version >= 2)
                {
                    var inChans  = new Dictionary<string, int>();
                    var outChans = new Dictionary<string, int>();
                    int inCnt  = r.ReadInt32();
                    for (int i = 0; i < inCnt;  i++) { string nm = r.ReadString(); inChans[nm]  = r.ReadInt32(); }
                    int outCnt = r.ReadInt32();
                    for (int o = 0; o < outCnt; o++) { string nm = r.ReadString(); outChans[nm] = r.ReadInt32(); }

                    if (host.Machine != null)
                    {
                        foreach (var c in host.Machine.Inputs)
                        {
                            if (!inChans.TryGetValue(c.Source?.Name ?? "", out int ch)) continue;
                            _destChProp ??= c.GetType().GetProperty("DestinationChannel");
                            _destChProp?.SetValue(c, ch);
                        }
                        foreach (var c in host.Machine.Outputs)
                        {
                            if (!outChans.TryGetValue(c.Destination?.Name ?? "", out int ch)) continue;
                            _srcChProp ??= c.GetType().GetProperty("SourceChannel");
                            _srcChProp?.SetValue(c, ch);
                        }
                    }
                }

                RefreshTargetGains();
                Notify(nameof(Routing));
                Notify(nameof(InputLabels));
                Notify(nameof(OutputLabels));
                Notify(nameof(FadeTimeMs));
                Notify(nameof(ConfirmOnClear));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Pedal Patcher] Load failed: {ex.Message}");
            }
            finally { pendingLoad = null; }
        }
    }
}
