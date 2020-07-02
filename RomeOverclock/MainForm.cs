using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Windows.Forms;
using OpenLibSys;
using ZenStatesDebugTool;

namespace RomeOverclock
{
    public partial class MainForm : Form
    {

        private readonly Ols ols;
        private SMU smu;
        readonly Mutex hMutexPci;

        private uint CPUID;
        private int _coreCount;
        private string _cpuName;

        private Dictionary<string, Dictionary<string, Action>> _presetFunctions;

        private void CheckOlsStatus()
        {
            // Check support library status
            switch (ols.GetStatus())
            {
                case (uint) Ols.Status.NO_ERROR:
                    break;
                case (uint) Ols.Status.DLL_NOT_FOUND:
                    throw new ApplicationException("WinRing DLL_NOT_FOUND");
                case (uint) Ols.Status.DLL_INCORRECT_VERSION:
                    throw new ApplicationException("WinRing DLL_INCORRECT_VERSION");
                case (uint) Ols.Status.DLL_INITIALIZE_ERROR:
                    throw new ApplicationException("WinRing DLL_INITIALIZE_ERROR");
            }

            // Check WinRing0 status
            switch (ols.GetDllStatus())
            {
                case (uint) Ols.OlsDllStatus.OLS_DLL_NO_ERROR:
                    break;
                case (uint) Ols.OlsDllStatus.OLS_DLL_DRIVER_NOT_LOADED:
                    throw new ApplicationException("WinRing OLS_DRIVER_NOT_LOADED");
                case (uint) Ols.OlsDllStatus.OLS_DLL_UNSUPPORTED_PLATFORM:
                    throw new ApplicationException("WinRing OLS_UNSUPPORTED_PLATFORM");
                case (uint) Ols.OlsDllStatus.OLS_DLL_DRIVER_NOT_FOUND:
                    throw new ApplicationException("WinRing OLS_DLL_DRIVER_NOT_FOUND");
                case (uint) Ols.OlsDllStatus.OLS_DLL_DRIVER_UNLOADED:
                    throw new ApplicationException("WinRing OLS_DLL_DRIVER_UNLOADED");
                case (uint) Ols.OlsDllStatus.OLS_DLL_DRIVER_NOT_LOADED_ON_NETWORK:
                    throw new ApplicationException("WinRing DRIVER_NOT_LOADED_ON_NETWORK");
                case (uint) Ols.OlsDllStatus.OLS_DLL_UNKNOWN_ERROR:
                    throw new ApplicationException("WinRing OLS_DLL_UNKNOWN_ERROR");
            }
        }

        private uint GetCpuInfo()
        {
            uint eax = 0, ebx = 0, ecx = 0, edx = 0;
            ols.CpuidPx(0x00000000, ref eax, ref ebx, ref ecx, ref edx, (UIntPtr) 1);
            if (ols.CpuidPx(0x00000001, ref eax, ref ebx, ref ecx, ref edx, (UIntPtr) 1) == 1)
            {
                return eax;
            }

            return 0;
        }

        private bool SmuWriteReg(uint addr, uint data, uint PCI_ADDR)
        {
            int res = 0;

            // Clear response
            res = ols.WritePciConfigDwordEx(PCI_ADDR, smu.SMU_OFFSET_ADDR, addr);
            if (res == 1)
            {
                res = ols.WritePciConfigDwordEx(PCI_ADDR, smu.SMU_OFFSET_DATA, data);
            }

            return (res == 1);
        }

        private bool SmuReadReg(uint addr, ref uint data, uint PCI_ADDR)
        {
            int res = 0;

            // Clear response
            res = ols.WritePciConfigDwordEx(PCI_ADDR, smu.SMU_OFFSET_ADDR, addr);
            if (res == 1)
            {
                res = ols.ReadPciConfigDwordEx(PCI_ADDR, smu.SMU_OFFSET_DATA, ref data);
            }

            return (res == 1);
        }

        private bool SmuWaitDone(uint PCI_ADDR)
        {
            bool res = false;
            ushort timeout = 1000;
            uint data = 0;
            while ((!res || data != 1) && --timeout > 0)
            {
                res = SmuReadReg(smu.SMU_ADDR_RSP, ref data, PCI_ADDR);
            }

            if (timeout == 0 || data != 1) res = false;

            return res;
        }

        private bool SmuRead(uint msg, ref uint data, uint PCI_ADDR)
        {
            bool res;

            // Clear response
            res = SmuWriteReg(smu.SMU_ADDR_RSP, 0, PCI_ADDR);

            if (res)
            {
                // Send message
                res = SmuWriteReg(smu.SMU_ADDR_MSG, msg, PCI_ADDR);
                if (res)
                {
                    // Check completion
                    res = SmuWaitDone(PCI_ADDR);

                    if (res)
                    {
                        res = SmuReadReg(smu.SMU_ADDR_ARG0, ref data, PCI_ADDR);
                    }
                }
            }

            return res;
        }

        private bool SmuWrite(uint msg, uint value, uint PCI_ADDR)
        {
            bool res;

            // Mutex
            res = hMutexPci.WaitOne(5000);

            // Clear response
            if (res)
            {
                res = SmuWriteReg(smu.SMU_ADDR_RSP, 0, PCI_ADDR);
            }

            if (res)
            {
                // Write data
                SmuWriteReg(smu.SMU_ADDR_ARG0, value, PCI_ADDR);

                // Send message
                res = SmuWriteReg(smu.SMU_ADDR_MSG, msg, PCI_ADDR);

                if (res)
                {
                    res = SmuWaitDone(PCI_ADDR);
                }
            }

            hMutexPci.ReleaseMutex();

            return res;
        }

        private int CountDecimals(decimal x)
        {
            var precision = 0;

            while (x * (decimal) Math.Pow(10, precision) !=
                   Math.Round(x * (decimal) Math.Pow(10, precision)))
                precision++;
            return precision;
        }

        private void PopulateFrequencyList(ComboBox.ObjectCollection l)
        {
            for (var i = 1800; i <= 4000; i += 50)
            {
                var v = i / 1000.00;
                l.Add(new FrequencyListItem(i, $"{v:0.00} GHz"));
            }
        }

        private void PopulateCCDList(ComboBox.ObjectCollection l)
        {
            for (var i = 0; i < _coreCount; i++)
            {
                l.Add(new CoreListItem(i / 8, i / 4, i));
            }
        }

        private void PopulateVoltageList(ComboBox.ObjectCollection l)
        {
            for (var i = 12; i < 128; i += 1)
            {
                var voltage = (decimal) (1.55 - i * 0.00625);
                int decimals = CountDecimals(voltage);

                if (decimals <= 3)
                {
                    l.Add(new VoltageListItem(i, (double) voltage));
                }
            }
        }

        private void SetStatus(string status, int socket = 1)
        {
            statusLabel.Text = status;
            logBox.Text = $@"- Socket {socket}: " + status + Environment.NewLine + logBox.Text;
        }

        private void HandleError(Exception ex, string title = "Error")
        {
            SetStatus("ERROR!");
            MessageBox.Show(ex.Message, title);
        }

        private void SetButtonsEnabled(bool enabled)
        {
            applyAllBtn.Enabled = enabled;
            applyACBtn.Enabled = enabled;
            //applySCBtn.Enabled = enabled;
            //applyVoltageBtn.Enabled = enabled;
            revertACBtn.Enabled = enabled;
            dualsocketCheck.Enabled = enabled;
            fullPerfBtn.Enabled = enabled;
            applyLockBtn.Enabled = enabled;
            revertVoltageBtn.Enabled = enabled;
            presetApplyBtn.Enabled = enabled;
        }

        private void SetFormsEnabled(bool enabled)
        {
            /*applyACBtn.Enabled = enabled;
            //applySCBtn.Enabled = enabled;
            //applyVoltageBtn.Enabled = enabled;
            revertACBtn.Enabled = enabled;

            AC_freqSelect.Enabled = enabled;
            SC_freqSelect.Enabled = enabled;
            SC_coreSelect.Enabled = enabled;
            dualsocketCheck.Enabled = enabled;
            fullPerfBtn.Enabled = enabled;
            applyLockBtn.Enabled = enabled;
            revertVoltageBtn.Enabled = enabled;
            presetApplyBtn.Enabled = enabled;*/
        }

        private void InitDefaultForm()
        {
            SetStatus("Loading...");

            PopulateFrequencyList(AC_freqSelect.Items);
            //PopulateFrequencyList(SC_freqSelect.Items);
            //PopulateCCDList(SC_coreSelect.Items);
            //PopulateVoltageList(voltageSelect.Items);
            AC_freqSelect.SelectedIndex = 16;
            //voltageSelect.SelectedIndex = 0;
            presetCpuSelect.SelectedIndex = 0;
            presetPresetSelect.SelectedIndex = 0;
            SetButtonsEnabled(true);
            SetFormsEnabled(false);

            //PPT_inp.Controls[0].Visible = false;
            //TDC_inp.Controls[0].Visible = false;
            //EDC_inp.Controls[0].Visible = false;
            voltageInp.Controls[0].Visible = false;

            SetStatus("Ready.");
        }

        private SMU.Status ApplySMUCommand(uint command, uint arg, uint PCI_ADDR)
        {
            try
            {
                if (SmuWrite(command, arg, PCI_ADDR))
                {
                    // Read response
                    uint data = 0;

                    if (SmuReadReg(smu.SMU_ADDR_RSP, ref data, PCI_ADDR))
                    {
                        //string responseString = "0x" + Convert.ToString(data, 16).ToUpper();
                        var status = (SMU.Status) data;
                        //SetStatus(GetSMUStatus.GetByType(status));

                        return status;

                        /*SmuReadReg(SMU_ADDR_ARG0, ref data);
                        ShowResultMessageBox(data);*/
                    }

                    SetStatus("Error reading response!");
                }
                else
                {
                    SetStatus("Error on writing SMU!");
                }
            }
            catch (ApplicationException e)
            {
                HandleError(e);
            }

            return SMU.Status.FAILED;
        }

        private void ApplyFrequencyAllCoreSetting(int frequency)
        {
            var freq_hex = Convert.ToUInt32(frequency);
            if (ApplySMUCommand(0x18, freq_hex, smu.SMU_PCI_ADDR) == SMU.Status.OK)
            {
                SetStatus($"Set frequency to {frequency} MHz!");
            }

            if (dualsocketCheck.Checked)
            {
                if (ApplySMUCommand(0x18, freq_hex, smu.SMU_PCI_ADDR_2) == SMU.Status.OK)
                {
                    SetStatus($"Set frequency to {frequency} MHz!", 2);
                }
            }
        }

        /**
         * Doesn't work as intended because coresnumber != ccd number
         */
        private void ApplyFrequencySingleCoreSetting(CoreListItem i, int frequency)
        {
            var data = Convert.ToUInt32(((i.CCD << 4 | (i.CCX % 2) & 0xf) << 4 | (i.CORE % 4) & 0xf) << 0x14 |
                                        frequency & 0xfffff);
            if (ApplySMUCommand(0x16, data, smu.SMU_PCI_ADDR) == SMU.Status.OK)
            {
                SetStatus($"Set core {i} frequency to {frequency} MHz!");
            }
        }

        /**
         * DO NOT USE, COULD BE DANGEROUS
         */
        private void ApplyFullPerfSetting(int apply = 1) // 0 = false, 1 = true
        {
            if (ValueSaver.GetValue("FullPerf") == apply) return;
            if (ApplySMUCommand(0xA, Convert.ToUInt32(400), smu.SMU_PCI_ADDR) == SMU.Status.OK)
            {
                ValueSaver.AddValue("FullPerf", apply);
                SetStatus("Unlocked full performance.");
            }

            if (dualsocketCheck.Checked)
            {
                if (ApplySMUCommand(0xA, Convert.ToUInt32(400), smu.SMU_PCI_ADDR_2) == SMU.Status.OK)
                {
                    SetStatus("Unlocked full performance.", 2);
                }
            }
        }

        private void ApplyPPTSetting(int val, decimal min, decimal max)
        {
            if (val < min || val > max) return;
            //if (ValueSaver.GetValue("PPT") == val) return;
            if (ApplySMUCommand(0x53, Convert.ToUInt32(val * 1000), smu.SMU_PCI_ADDR) == SMU.Status.OK)
            {
                ValueSaver.AddValue("PPT", val);
                SetStatus($"Set PPT limit to {val} W.");
            }

            if (dualsocketCheck.Checked)
            {
                if (ApplySMUCommand(0x53, Convert.ToUInt32(val * 1000), smu.SMU_PCI_ADDR_2) == SMU.Status.OK)
                {
                    SetStatus($"Set PPT limit to {val} W.", 2);
                }
            }
        }

        private void ApplyTDCSetting(int val, decimal min, decimal max)
        {
            if (val < min || val > max) return;
            //if (ValueSaver.GetValue("TDC") == val) return;
            if (ApplySMUCommand(0x54, Convert.ToUInt32(val * 1000), smu.SMU_PCI_ADDR) == SMU.Status.OK)
            {
                ValueSaver.AddValue("TDC", val);
                SetStatus($"Set TDC limit to {val} A.");
            }

            if (dualsocketCheck.Checked)
            {
                if (ApplySMUCommand(0x54, Convert.ToUInt32(val * 1000), smu.SMU_PCI_ADDR_2) == SMU.Status.OK)
                {
                    SetStatus($"Set TDC limit to {val} A.", 2);
                }
            }
        }

        private void ApplyEDCSetting(int val, decimal min, decimal max)
        {
            if (val < min || val > max) return;
            //if (ValueSaver.GetValue("EDC") == val) return;
            if (ApplySMUCommand(0x55, Convert.ToUInt32(val * 1000), smu.SMU_PCI_ADDR) == SMU.Status.OK)
            {
                ValueSaver.AddValue("EDC", val);
                SetStatus($"Set EDC limit to {val} A.");
            }

            if (dualsocketCheck.Checked)
            {
                if (ApplySMUCommand(0x55, Convert.ToUInt32(val * 1000), smu.SMU_PCI_ADDR_2) == SMU.Status.OK)
                {
                    SetStatus($"Set EDC limit to {val} A.", 2);
                }
            }
        }

        private void ApplyVoltage(decimal vol, decimal min, decimal max)
        {
            int val = (int) ((vol - (decimal) 1.55) / (decimal) -0.00625);
            decimal voltage = vol;
            if (val < min || val > max) return;
            if (ValueSaver.GetValue("voltage") == val) return;
            if (ApplySMUCommand(0x12, Convert.ToUInt32(val), smu.SMU_PCI_ADDR) == SMU.Status.OK)
            {
                ValueSaver.AddValue("voltage", val);
                SetStatus($"Set Voltage to {voltage} V.", 1);
            }

            if (dualsocketCheck.Checked)
            {
                if (ApplySMUCommand(0x12, Convert.ToUInt32(val), smu.SMU_PCI_ADDR_2) == SMU.Status.OK)
                {
                    SetStatus($"Set Voltage to {voltage} V.", 2);
                }
            }
        }

        private void RevertVoltage()
        {
            if (ApplySMUCommand(0x13, 1, smu.SMU_PCI_ADDR) == SMU.Status.OK)
            {
                SetStatus("Reverted voltage to normal.", 1);
            }

            if (dualsocketCheck.Checked)
            {
                if (ApplySMUCommand(0x13, 1, smu.SMU_PCI_ADDR_2) == SMU.Status.OK)
                {
                    SetStatus("Reverted voltage to normal.", 2);
                }
            }
        }

        private void RevertFrequency()
        {
            if (ApplySMUCommand(0x19, 1, smu.SMU_PCI_ADDR) == SMU.Status.OK)
            {
                SetStatus("Reverted frequency to normal.", 1);
            }

            if (dualsocketCheck.Checked)
            {
                if (ApplySMUCommand(0x19, 1, smu.SMU_PCI_ADDR_2) == SMU.Status.OK)
                {
                    SetStatus("Reverted frequency to normal.", 2);
                }
            }
        }

        private void ApplyFreqLock(bool check)
        {
            if (check)
            {
                if (ValueSaver.GetValue("FREQ_LOCK") == 1) return;
                if (ApplySMUCommand(0x24, 1, smu.SMU_PCI_ADDR) == SMU.Status.OK)
                {
                    ValueSaver.AddValue("FREQ_LOCK", 1);
                    SetStatus("Locked frequencies.");
                }

                if (dualsocketCheck.Checked)
                {
                    if (ApplySMUCommand(0x24, 1, smu.SMU_PCI_ADDR_2) == SMU.Status.OK)
                    {
                        SetStatus("Locked frequencies.", 2);
                    }
                }
            }
            else
            {
                if (ValueSaver.GetValue("FREQ_LOCK") == 0) return;
                if (ApplySMUCommand(0x25, 1, smu.SMU_PCI_ADDR) == SMU.Status.OK)
                {
                    ValueSaver.AddValue("FREQ_LOCK", 0);
                    SetStatus("Unlocked frequencies.");
                }

                if (dualsocketCheck.Checked)
                {
                    if (ApplySMUCommand(0x25, 1, smu.SMU_PCI_ADDR_2) == SMU.Status.OK)
                    {
                        SetStatus("Unlocked frequencies.", 2);
                    }
                }
            }
        }

        public MainForm()
        {
            InitializeComponent();
            ols = new Ols();
            hMutexPci = new Mutex();
            smu = new Zen2Settings();

            try
            {
                CheckOlsStatus();
            }
            catch (ApplicationException ex)
            {
                MessageBox.Show(ex.Message, @"Error");
                Dispose();
                Application.Exit();
            }

            CPUID = GetCpuInfo() & 0xFFFFFFF0;
#if !DEBUG
            if (CPUID != 0x00830F00 && CPUID != 0x00830F10) // EPYC Rome ES
            {
                MessageBox.Show(@"CPU not supported!", @"Error");
                Dispose();
                Application.Exit();
                return;
            }
#endif
#if DEBUG
            var res = MessageBox.Show(
                @"This is an experimental version of this software. Keep in mind that it is possible to encounter bugs.",
                @"Warning", MessageBoxButtons.OKCancel);
            if (res != DialogResult.OK)
            {
                Dispose();
                Application.Exit();
            }
#endif

            var mg = new ManagementObjectSearcher("Select * from Win32_Processor").Get()
                .Cast<ManagementBaseObject>();
            _coreCount = mg.Sum(item => int.Parse(item["NumberOfCores"].ToString()));
            _cpuName = (string)mg.First()["Name"];
            _cpuName = _cpuName.Replace("AMD Eng Sample: ", "").Trim();

            InitDefaultForm();
            
            _presetFunctions = new Dictionary<string, Dictionary<string, Action>>
            {
                {"64", new Dictionary<string, Action>
                {
                    {"High Multi-core", () =>
                    {
                        ApplyFreqLock(false);
                        ApplyVoltage((decimal)1.05, 12, 128);
                        ApplyFrequencyAllCoreSetting(3800);

                        ApplyEDCSetting(Convert.ToInt32(30), 0, 800);
                        ApplyTDCSetting(0, 0, 600);
                        ApplyPPTSetting(0, 0, 1500);
                    }},
                    {"Best of both", () =>
                    {
                        RevertVoltage();
                        if (_cpuName.StartsWith("2S1")) ApplyVoltage((decimal)1.05, 12, 128);
                        ApplyFrequencyAllCoreSetting(3200);
                        ApplyFreqLock(true);
                        
                        ApplyEDCSetting(Convert.ToInt32(600), 0, 800);
                        ApplyTDCSetting(Convert.ToInt32(0), 0, 600);
                        ApplyPPTSetting(Convert.ToInt32(0), 0, 1500);
                    }},
                    {"High Single-core", () =>
                    {
                        RevertVoltage();
                        if (_cpuName.StartsWith("2S1")) ApplyVoltage((decimal)1.1, 12, 128); // ?
                        ApplyFrequencyAllCoreSetting(3400);
                        ApplyFreqLock(true);
                        
                        ApplyEDCSetting(Convert.ToInt32(700), 0, 700);
                        ApplyTDCSetting(Convert.ToInt32(700), 0, 700);
                        ApplyPPTSetting(Convert.ToInt32(1500), 0, 1500);
                    }}
                }},
                {"48", new Dictionary<string, Action>
                {
                    {"High Multi-core", () =>
                    {
                        ApplyVoltage((decimal)1.05, 12, 128);
                        ApplyFrequencyAllCoreSetting(3800);
                        ApplyFreqLock(true);
                        
                        ApplyEDCSetting(Convert.ToInt32(45), 0, 200);
                        ApplyTDCSetting(0, 0, 1);
                        ApplyPPTSetting(0, 0, 1);
                    }},
                    {"Best of both", () =>
                    {
                        ApplyFreqLock(false);
                        RevertVoltage();
                        if (_cpuName.StartsWith("2S1")) ApplyVoltage((decimal)1.05, 12, 128);
                        ApplyFrequencyAllCoreSetting(3300);
                        
                        ApplyEDCSetting(Convert.ToInt32(600), 0, 800);
                        ApplyTDCSetting(Convert.ToInt32(0), 0, 600);
                        ApplyPPTSetting(Convert.ToInt32(0), 0, 1500);
                    }},
                    {"High Single-core", () =>
                    {
                        ApplyFreqLock(false);
                        RevertVoltage();
                        if (_cpuName.StartsWith("2S1")) ApplyVoltage((decimal)1.1, 12, 128); // ?
                        ApplyFrequencyAllCoreSetting(3500);
                        
                        ApplyEDCSetting(Convert.ToInt32(700), 0, 700);
                        ApplyTDCSetting(Convert.ToInt32(700), 0, 700);
                        ApplyPPTSetting(Convert.ToInt32(1500), 0, 1500);
                    }}
                }},
                {"32", new Dictionary<string, Action>
                {
                    {"High Multi-core", () =>
                    {
                        RevertVoltage();
                        if (_cpuName.StartsWith("2S1")) ApplyVoltage((decimal)1.05, 12, 128);
                        ApplyFrequencyAllCoreSetting(3450);
                        ApplyFreqLock(true);
                        
                        ApplyEDCSetting(Convert.ToInt32(600), 0, 700);
                        ApplyTDCSetting(Convert.ToInt32(600), 0, 700);
                        ApplyPPTSetting(Convert.ToInt32(1500), 0, 1500);
                    }},
                    {"Best of both", () =>
                    {
                        ApplyFreqLock(false);
                        RevertVoltage();
                        if (_cpuName.StartsWith("2S1")) ApplyVoltage((decimal)1.05, 12, 128);
                        ApplyFrequencyAllCoreSetting(3300);
                        
                        ApplyEDCSetting(Convert.ToInt32(600), 0, 800);
                        ApplyTDCSetting(Convert.ToInt32(0), 0, 600);
                        ApplyPPTSetting(Convert.ToInt32(0), 0, 1500);
                    }},
                    {"High Single-core", () =>
                    {
                        ApplyFreqLock(false);
                        RevertVoltage();
                        if (_cpuName.StartsWith("2S1")) ApplyVoltage((decimal)1.1, 12, 128); // ?
                        ApplyFrequencyAllCoreSetting(3500);
                        
                        ApplyEDCSetting(Convert.ToInt32(700), 0, 700);
                        ApplyTDCSetting(Convert.ToInt32(700), 0, 700);
                        ApplyPPTSetting(Convert.ToInt32(1500), 0, 1500);
                    }}
                }}
            };
        }

        private void applyBtn_Click(object sender, EventArgs e)
        {
            ApplyVoltage(voltageInp.Value, 12, 128);
            SetFormsEnabled(true);
        }

        private void applyACBtn_Click(object sender, EventArgs e)
        {
            ApplyFrequencyAllCoreSetting(((FrequencyListItem)AC_freqSelect.SelectedItem).frequency);
        }

        private void applySCBtn_Click(object sender, EventArgs e)
        {
            //ApplyFrequencySingleCoreSetting((CoreListItem)SC_coreSelect.SelectedItem, ((FrequencyListItem)SC_freqSelect.SelectedItem).frequency);
        }

        private void applyVoltageBtn_Click(object sender, EventArgs e)
        {
            ApplyVoltage(voltageInp.Value, 12, 128);
        }

        private void revertACBtn_Click(object sender, EventArgs e)
        {
            RevertFrequency();
        }

        private void fullPerfBtn_Click(object sender, EventArgs e)
        {
            ApplyPPTSetting(Convert.ToInt32(PPT_inp.Value), PPT_inp.Minimum, PPT_inp.Maximum);
            ApplyTDCSetting(Convert.ToInt32(TDC_inp.Value), TDC_inp.Minimum, TDC_inp.Maximum);
            ApplyEDCSetting(Convert.ToInt32(EDC_inp.Value), EDC_inp.Minimum, EDC_inp.Maximum);
            //ApplyEDCSetting(Convert.ToInt32(30), 0, 200);
            //ApplyFreqLock();
            //ApplyFullPerfSetting();
        }

        private void applyLockBtn_Click(object sender, EventArgs e)
        {
            ApplyFreqLock(overclockCheck.Checked);
        }

        private void revertVoltageBtn_Click(object sender, EventArgs e)
        {
            RevertVoltage();
        }

        private void presetApplyBtn_Click(object sender, EventArgs e)
        {
            var presetCPU = presetCpuSelect.Text;
            var presetPreset = presetPresetSelect.Text;
            SetStatus(presetCPU + " " + presetPreset);
            var cores = presetCPU.Substring(0, 2);

            _presetFunctions[cores][presetPreset]();
        }
    }
}