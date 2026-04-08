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
        internal readonly float[,] VuLevel = new float[NumInputs, NumOutputs];

        bool[,] clipboard;
        CMachineStateData pendingLoad;
        int _currentPatch;

        public event PropertyChangedEventHandler PropertyChanged;
        void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        // ── Construction ──────────────────────────────────────────────────────
        public CMachine(IBuzzMachineHost host)
        {
            this.host = host;
            for (int i = 0; i < NumInputs;  i++) inputLabels[i]  = $"In {i + 1}";
            for (int o = 0; o < NumOutputs; o++) outputLabels[o] = $"Out {o + 1}";

            // Set channel counts when this machine is added to the song
            // (host.Machine is null during construction)
            Global.Buzz.Song.MachineAdded += OnMachineAdded;
            Global.Buzz.Song.MachineRemoved += OnMachineRemoved;
        }

        void OnMachineAdded(IMachine m)
        {
            if (m != host.Machine) return;
            host.InputChannelCount  = NumInputs;
            host.OutputChannelCount = NumOutputs;
        }

        void OnMachineRemoved(IMachine m)
        {
            if (m != host.Machine) return;
            Global.Buzz.Song.MachineAdded   -= OnMachineAdded;
            Global.Buzz.Song.MachineRemoved -= OnMachineRemoved;
        }

        // ── Channel naming (shown in ReBuzz connection circle tooltip) ────────
        public string GetChannelName(bool input, int index)
        {
            if (input  && index >= 0 && index < NumInputs)  return inputLabels[index];
            if (!input && index >= 0 && index < NumOutputs) return outputLabels[index];
            return "";
        }

        // ── Global parameter ──────────────────────────────────────────────────
        [ParameterDecl(IsStateless = false, Name = "Patch",
                       MinValue = 0, MaxValue = NumPatches - 1, DefValue = 0,
                       Description = "Active patch (0-47)")]
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
                           Description = "High byte = input (1-based), Low byte = output (1-based)")]
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
        // EffectBlockMulti signature — ReBuzz provides per-channel buffers directly.
        // input[i] is the Sample[] for input channel i (null if nothing connected).
        // output[o] is the Sample[] for output channel o (null if nothing connected).
        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            if (Tracks != null)
                foreach (var t in Tracks)
                    if (t.Command != 255) RunCommand(t.Command, t.Argument);

            float sr    = host.MasterInfo.SamplesPerSec;
            float fadeN = _fadeTimeMs > 0 ? _fadeTimeMs * sr / 1000f : 1f;
            float step  = 1f / fadeN;
            bool anyActive = false;

            // Zero all output buffers
            for (int o = 0; o < NumOutputs; o++)
            {
                if (o >= output.Count || output[o] == null) continue;
                for (int s = 0; s < n; s++) { output[o][s].L = 0f; output[o][s].R = 0f; }
            }

            for (int i = 0; i < NumInputs; i++)
            {
                if (i >= input.Count || input[i] == null) continue;

                for (int o = 0; o < NumOutputs; o++)
                {
                    if (o >= output.Count || output[o] == null) continue;

                    float g  = gain[i, o];
                    float tg = targetGain[i, o];
                    if (g == 0f && tg == 0f) { VuLevel[i, o] *= 0.85f; continue; }

                    float peak = 0f;
                    for (int s = 0; s < n; s++)
                    {
                        if      (g < tg) g = MathF.Min(g + step, tg);
                        else if (g > tg) g = MathF.Max(g - step, tg);
                        output[o][s].L += input[i][s].L * g;
                        output[o][s].R += input[i][s].R * g;
                        float lv = MathF.Abs(input[i][s].L);
                        float rv = MathF.Abs(input[i][s].R);
                        if (lv > peak) peak = lv;
                        if (rv > peak) peak = rv;
                    }
                    gain[i, o] = g;
                    VuLevel[i, o] = MathF.Max(peak, VuLevel[i, o] * 0.85f);
                    anyActive = true;
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
                if (version == 1) r.ReadBoolean(); // _autoName removed in v2 — discard
                for (int i = 0; i < NumInputs;  i++) inputLabels[i]  = r.ReadString();
                for (int o = 0; o < NumOutputs; o++) outputLabels[o] = r.ReadString();
                for (int p = 0; p < NumPatches; p++)
                for (int i = 0; i < NumInputs;  i++)
                for (int o = 0; o < NumOutputs; o++)
                    routing[p, i, o] = r.ReadBoolean();
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
