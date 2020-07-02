﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ZenStatesDebugTool
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "<Pending>")]
    public abstract class SMU
    {
        public enum CPUType : int
        { 
            Unsupported     = 0,
            DEBUG,
            Rome
        };

        public enum Status : int
        {
            OK                      = 0x1,
            FAILED                  = 0xFF,
            UNKNOWN_CMD             = 0xFE,
            CMD_REJECTED_PREREQ     = 0xFD,
            CMD_REJECTED_BUSY       = 0xFC
        }

        public SMU()
        {
            Version = 0;
            // SMU
            SMU_PCI_ADDR = ((0x0&0xFF)<<8) | ((0x0&0x1F)<<3) | (0x0&7);//0x00000000;
            SMU_PCI_ADDR_2 = ((0xA0&0xFF)<<8) | ((0x0&0x1F)<<3) | (0x0&7);//0x00000000;
            SMU_OFFSET_ADDR = 0xB8;
            SMU_OFFSET_DATA = 0xBC;

            SMU_ADDR_MSG = 0x03B10528;
            SMU_ADDR_RSP = 0x03B10564;
            SMU_ADDR_ARG0 = 0x03B10598;
            SMU_ADDR_ARG1 = SMU_ADDR_ARG0 + 0x4;

            // SMU Messages
            SMC_MSG_TestMessage = 0x1;
            SMC_MSG_GetSmuVersion = 0x2;
        }

        public uint Version { get; set; }
        public uint SMU_PCI_ADDR { get; protected set; }
        public uint SMU_PCI_ADDR_2 { get; protected set; }
        public uint SMU_OFFSET_ADDR { get; protected set; }
        public uint SMU_OFFSET_DATA { get; protected set; }

        public uint SMU_ADDR_MSG { get; protected set; }
        public uint SMU_ADDR_RSP { get; protected set; }
        public uint SMU_ADDR_ARG0 { get; protected set; }

        public uint SMU_ADDR_ARG1 { get; protected set; }

        public uint SMC_MSG_TestMessage { get; protected set; }
        public uint SMC_MSG_GetSmuVersion { get; protected set; }
    }

    public class Zen2Settings : SMU
    {
        public Zen2Settings()
        {
            SMU_ADDR_MSG = 0x03B10524;
            SMU_ADDR_RSP = 0x03B10570;
            SMU_ADDR_ARG0 = 0x03B10A40;
            SMU_ADDR_ARG1 = SMU_ADDR_ARG0 + 0x4;
        }
    }

    public static class GetSMUStatus
    {
        private static readonly Dictionary<SMU.Status, String> status = new Dictionary<SMU.Status, string>()
        {
            { SMU.Status.OK, "OK" },
            { SMU.Status.FAILED, "Failed" },
            { SMU.Status.UNKNOWN_CMD, "Unknown Command" },
            { SMU.Status.CMD_REJECTED_PREREQ, "CMD Rejected Prereq" },
            { SMU.Status.CMD_REJECTED_BUSY, "CMD Rejected Busy" }
        };

        public static string GetByType(SMU.Status type)
        {
            if (!status.TryGetValue(type, out string output))
            {
                return "Unknown Status";
            }
            return output;
        }
    }
}
