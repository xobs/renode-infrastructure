//
// Copyright (c) 2010-2020 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.IO;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Sensor;
using Antmicro.Renode.Peripherals.I2C;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Sensors
{
    public class OV2640 : II2CPeripheral, IProvidesRegisterCollection<ByteRegisterCollection>, ISensor
    {
        public OV2640(IPeripheral parent)
        {
            this.parent = parent;

            dspRegisters = new ByteRegisterCollection(this);
            sensorRegisters = new ByteRegisterCollection(this);

            DefineDSPRegisters();
            DefineSensorRegisters();
        }

        public void FinishTransmission()
        {
            parent.NoisyLog("OV2640: Finishing transmission, going idle");
            state = State.Idle;
        }

        public void Write(byte[] data)
        {
            parent.NoisyLog("OV2640: Received the following bytes: {0}", Misc.PrettyPrintCollectionHex(data));
            foreach(var d in data)
            {
                Write(d);
            }
        }

        public byte[] Read(int count)
        {
            parent.NoisyLog("OV2640: Reading from the device in state {0}", state);
            switch(state)
            {
                case State.Processing:
                {
                    var result = RegistersCollection.Read(address);
                    parent.NoisyLog("OV2640: Read 0x{0:X} from register {1}", result, DecodeRegister(address));
                    return new [] { result };
                }

                default:
                    parent.Log(LogLevel.Error, "OV2640: Reading in an unexpected state: {0}", state);
                    return new byte[0];
            }
        }

        public void Reset()
        {
            state = State.Idle;
            sensorRegisterBankSelected = false;
            address = 0;

            dspRegisters.Reset();
            sensorRegisters.Reset();
        }

        public ByteRegisterCollection RegistersCollection => sensorRegisterBankSelected ? sensorRegisters : dspRegisters;

        private void Write(byte b)
        {
            parent.NoisyLog("OV2640: Received byte 0x{0:X} in state {1}", b, state);
            switch(state)
            {
                case State.Idle:
                {
                    address = b;
                    state = State.Processing;

                    parent.NoisyLog("OV2640: Selected register {0}", DecodeRegister(address));
                    break;
                }

                case State.Processing:
                {
                    parent.NoisyLog("OV2640: Writing 0x{0:X} to register {1}", b, DecodeRegister(address));
                    RegistersCollection.Write(address, b);
                    break;
                }

                default:
                    parent.Log(LogLevel.Error, "OV2640: Writing byte in an unexpected state: {0}", state);
                    break;
            }
        }

        private void DefineDSPRegisters()
        {
            DSPRegister.RegisterBankSelect.Define(dspRegisters)
                .WithFlag(0, name: "Register bank select",
                    valueProviderCallback: _ => sensorRegisterBankSelected,
                    writeCallback: (_, val) =>
                    {
                        sensorRegisterBankSelected = val;
                        parent.NoisyLog("OV2640: Register bank selected: {0}", sensorRegisterBankSelected ? "sensor" : "DSP");
                    })
                .WithReservedBits(1, 7)
            ;
        }

        private void DefineSensorRegisters()
        {
            SensorRegister.COM7.Define(sensorRegisters)
                .WithReservedBits(0, 1)
                .WithTag("Color bar test pattern", 1, 1)
                .WithTag("Zoom mode", 2, 1)
                .WithReservedBits(3, 1)
                .WithTag("Resolution selection", 4, 3)
                .WithFlag(7, name: "SRST", writeCallback: (_, val) => 
                { 
                    if(!val) 
                    {
                        return;
                    }

                    parent.NoisyLog("OV2640: Initiating system reset");
                    Reset(); 
                })
            ;
        }

        private string DecodeRegister(byte address)
        {
            return "{0} (0x{1:X})".FormatWith(sensorRegisterBankSelected
                                                 ? ((SensorRegister)address).ToString()
                                                 : ((DSPRegister)address).ToString(),
                                             address);
        }

        private State state;
        private bool sensorRegisterBankSelected;
        private byte address;

        private readonly ByteRegisterCollection dspRegisters;
        private readonly ByteRegisterCollection sensorRegisters;

        private readonly IPeripheral parent;

        private enum State
        {
            Idle,
            Processing
        }

        private enum DSPRegister
        {
            // unlisted registers are reserved
            BypassDSP = 0x5,

            QuantizationScaleFactor = 0x44,

            ControlI = 0x50,
            HorizontalSizeLow = 0x51,
            VerticalSizeLow = 0x52,
            OffsetXLow = 0x53,
            OffsetYLow = 0x54,
            SizeOffsetHigh = 0x55,

            DPRP = 0x56,
            Test = 0x57,

            ZMOW = 0x5A,
            ZMOH = 0x5B,
            ZMHH = 0x5C,

            SDEIndirectRegisterAccess_Address = 0x7C,
            SDEIndirectRegisterAccess_Data = 0x7D,

            Control2_ModuleEnable = 0x86,
            Control3_ModuleEnable = 0x87,

            SIZEL = 0x8C,

            HSIZE8 = 0xC0,
            VSIZE8 = 0xC1,
            Control0_ModuleEnable = 0xC2,
            Control1_ModuleEnable = 0xC3,

            R_DVP_SP = 0xD3,

            ImageMode = 0xDA,

            Reset = 0xE0,

            MS_SP = 0xF0,

            SS_ID = 0xF7,
            SS_CTRL = 0xF8,
            MC_BIST = 0xF9,
            MC_AL = 0xFA,
            MC_AH = 0xFB,
            MC_D = 0xFC,
            P_CMD = 0xFD,
            P_STATUS = 0xFE,
            RegisterBankSelect = 0xFF
        }

        private enum SensorRegister
        {
            // unlisted registers are reserved
            GAIN = 0x00,

            COM1 = 0x3,
            REG04 = 0x04,

            REG08 = 0x08,
            COM2 = 0x09,
            PIDH = 0x0A,
            PIDL = 0x0B,
            COM3 = 0x0C,
            COM4 = 0x0D,

            AEC = 0x10,
            CLKRC = 0x11,
            COM7 = 0x12,
            COM8 = 0x13,
            COM9 = 0x14,
            COM10 = 0x15,

            HREFST = 0x17,
            HREFEND = 0x18,
            VSTRT = 0x19,
            VEND = 0x1A,

            MIDH = 0x1C,
            MIDL = 0x1D,

            AEW = 0x24,
            AWB = 0x25,
            VV = 0x26,

            REG2A = 0x2A,
            FRARL = 0x2B,

            ADDVSL = 0x2D,
            ADDVSH = 0x2E,
            YAVG = 0x2F,
            HSDY = 0x30,
            HEDY = 0x31,
            REG32 = 0x32,

            ARCOM2 = 0x34,

            REG45 = 0x45,
            FLL = 0x46,
            FLH = 0x47,
            COM19 = 0x48,
            ZOOMS = 0x49,

            COM22 = 0x4B,

            COM25 = 0x4E,
            BD50 = 0x4F,
            BD60 = 0x50,

            REG5D = 0x5D,
            REG5E = 0x5E,
            REG5F = 0x5F,
            REG60 = 0x60,

            HISTO_LOW = 0x61,
            HISTO_HIGH = 0x62,
        }
    }
}
