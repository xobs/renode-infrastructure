//
// Copyright (c) 2010-2018 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class PSE_SPI : NullRegistrationPointPeripheralContainer<ISPIPeripheral>, IDoubleWordPeripheral, IKnownSize, ISPIPeripheral
    {
        public PSE_SPI(Machine machine) : base(machine)
        {
            locker = new object();
            receiveBuffer = new Queue<byte>();
            transmitBuffer = new Queue<byte>();
            IRQ = new GPIO();

            controlRegister = new DoubleWordRegister(this, 0x80000102)
                    .WithFlag(0, out coreEnabled,
                        changeCallback: (_, val) =>
                        {
                            if(!val)
                            {
                                frameCounter.Value = 0;
                            }
                        }, name: "ENABLE")
                    .WithFlag(1, out master, name: "MASTER")
                    .WithTag("MODE", 2, 2)
                    .WithFlag(4, out enableIrqOnReceive, name: "INTRXDATA")
                    .WithFlag(5, out enableIrqOnTransmit, name: "INTTXDATA")
                    .WithFlag(6, out enableIrqOnOverflow, name: "INTRCVOVFLOW")
                    .WithFlag(7, out enableIrqOnUnderrun, name: "INTTXTURUN")
                    .WithValueField(8, 16, out frameCounter,
                        writeCallback: (_, val) =>
                        {
                            framesReceived = 0;
                            framesTransmitted = 0;
                        }, name: "FRAMECNT")
                    .WithTag("SPO", 24, 1)
                    .WithTag("SPH", 25, 1)
                    .WithTag("SPS", 26, 1)
                    .WithTag("FRAMEURUN", 27, 1)
                    .WithTag("CLKMODE", 28, 1)
                    .WithFlag(29,
                        changeCallback: (_, val) =>
                        {
                            if(val)
                            {
                                if(frameSize.Value <= 8)
                                {
                                    fifoSize = 32;
                                }
                                else if(frameSize.Value >= 9 && frameSize.Value <= 16)
                                {
                                    fifoSize = 16;
                                }
                                else if(frameSize.Value >= 17)
                                {
                                    fifoSize = 8;
                                }
                            }
                            else
                            {
                                fifoSize = 4;
                            }
                        }, name: "BIGFIFO")
                    .WithTag("OENOFF", 30, 1)
                    .WithFlag(31, FieldMode.WriteOneToClear | FieldMode.Read, changeCallback: (_, __) => Reset(), name: "RESET");

            maskedInterruptRegister = new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, valueProviderCallback: (_) => enableIrqOnReceive.Value && transmitDone.Value, name: "TXDONE")
                    .WithFlag(1, FieldMode.Read, valueProviderCallback: (_) => enableIrqOnTransmit.Value && receiveDone.Value, name: "RXDONE")
                    .WithFlag(2, FieldMode.Read, valueProviderCallback: (_) => enableIrqOnOverflow.Value && receiveOverflow.Value, name: "RXOVERFLOW")
                    .WithFlag(3, FieldMode.Read, valueProviderCallback: (_) => enableIrqOnUnderrun.Value && transmitUnderrun.Value, name: "TXUNDERRUN")
                    .WithFlag(4, FieldMode.Read, valueProviderCallback: (_) => fullCommandReceived.Value, name: "CMDINT")
                    .WithFlag(5, FieldMode.Read, valueProviderCallback: (_) => false, name: "SSEND");

            var registersMap = new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.Control, controlRegister},

                {(long)Registers.FrameSize, new DoubleWordRegister(this, 0x4)
                    .WithValueField(0, 6, out frameSize, name: "FRAMESIZE")
                },

                {(long)Registers.Status, new DoubleWordRegister(this, 0x2440)
                    .WithFlag(0, out dataSent, FieldMode.Read, name: "TXDATASENT")
                    .WithFlag(1, out dataReceived, FieldMode.Read, name: "RXDATARCVD")
                    .WithFlag(2, FieldMode.Read, valueProviderCallback: (_) => receiveOverflow.Value, name: "RXOVERFLOW")
                    .WithFlag(3, FieldMode.Read, valueProviderCallback: (_) => transmitUnderrun.Value, name: "TXUNDERRUN")
                    .WithFlag(4, FieldMode.Read, valueProviderCallback: (_) => receiveBuffer.Count == frameSize.Value, name: "RXFIFOFULL")
                    .WithFlag(5, FieldMode.Read, valueProviderCallback: (_) => (receiveBuffer.Count + 1) == frameSize.Value, name: "RXFIFOFULLNEXT")
                    .WithFlag(6, FieldMode.Read, valueProviderCallback: (_) => receiveBuffer.Count == 0, name: "RXFIFOEMPTY")
                    .WithFlag(7, FieldMode.Read, valueProviderCallback: (_) => receiveBuffer.Count == 1, name: "RXFIFOEMPTYNEXT")
                    .WithFlag(8, FieldMode.Read, valueProviderCallback: (_) => transmitBuffer.Count == frameSize.Value, name: "TXFIFOFULL")
                    .WithFlag(9, FieldMode.Read, valueProviderCallback: (_) => (transmitBuffer.Count + 1) == frameSize.Value, name: "TXFIFOFULLNEXT")
                    .WithFlag(10, FieldMode.Read, valueProviderCallback: (_) => transmitBuffer.Count == 0, name: "TXFIFOEMPTY")
                    .WithFlag(11, FieldMode.Read, valueProviderCallback: (_) => transmitBuffer.Count == 1, name: "TXFIFOEMPTYNEXT")
                    .WithTag("FRAMESTART", 12, 1)
                    .WithTag("SSEL", 13, 1)
                    .WithTag("ACTIVE", 14, 1)
                },

                {(long)Registers.InterruptClear, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.WriteOneToClear, writeCallback: (_, __) => transmitDone.Value = false, name: "TXDONE")
                    .WithFlag(1, FieldMode.WriteOneToClear, writeCallback: (_, __) => receiveDone.Value = false, name: "RXDONE")
                    .WithFlag(2, FieldMode.WriteOneToClear, writeCallback: (_, __) => receiveOverflow.Value = false, name: "RXOVERFLOW")
                    .WithFlag(3, FieldMode.WriteOneToClear, writeCallback: (_, __) => transmitUnderrun.Value = false, name: "TXUNDERRUN")
                    .WithFlag(4, FieldMode.WriteOneToClear, writeCallback: (_, __) => fullCommandReceived.Value = false, name: "CMDINT")
                    .WithTag("SSEND", 5, 1)
                },

                {(long)Registers.ReceiveData, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read,
                        valueProviderCallback: _ =>
                        {
                            dataReceived.Value = false;
                            return receiveBuffer.Count > 0 ? receiveBuffer.Dequeue() : (byte)0x00;
                        }, name: "RXDATA")
                },

                {(long)Registers.TransmitData, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Write,
                        writeCallback: (_, val) =>
                        {
                            if(coreEnabled.Value)
                            {
                                dataSent.Value = false;
                                if(master.Value == true)
                                {
                                    this.Log(LogLevel.Noisy, $"Writing 0x{val:X} to slave.");
                                    //TODO: should probably enqueue data if frame counter is enabled and the frameTransmitted reached the threshold.
                                    // We do not handle TXFIFO as its handling is not clear from the documentation.
                                    TryReceive(RegisteredPeripheral.Transmit((byte)val));
                                    if(framesTransmitted == frameCounter.Value)
                                    {
                                        dataSent.Value = true;
                                        transmitDone.Value = true;
                                    }
                                }
                                else
                                {
                                    this.Log(LogLevel.Noisy, $"Writing 0x{val:X} to transmit buffer in slave mode.");
                                    transmitBuffer.Enqueue((byte)val);
                                }
                            }
                        }, name: "TXDATA")
                },

                {(long)Registers.ClockRate, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "CLKRATE")
                },

                {(long)Registers.SlaveSelect, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "SSEL")
                },

                {(long)Registers.InterruptMasked, maskedInterruptRegister},

                {(long)Registers.InterruptRaw, new DoubleWordRegister(this)
                    .WithFlag(0, out transmitDone, name: "TXDONE")
                    .WithFlag(1, out receiveDone, name: "RXDONE")
                    .WithFlag(2, out receiveOverflow, name: "RXOVERFLOW")
                    .WithFlag(3, out transmitUnderrun, name: "TXUNDERRUN")
                    .WithFlag(4, out fullCommandReceived, name: "CMDINT")
                    .WithTag("SSEND", 5, 1)
                },

                {(long)Registers.ControlBitsForEnhancedModes, new DoubleWordRegister(this)
                    .WithTag("AUTOSTATUS", 0, 1)
                    .WithTag("AUTOPOLL", 1, 1)
                    .WithFlag(2, out disableFrameCount, name: "DISFRMCNT")
                    // bit 3 reserved
                    .WithFlag(4, out enableIrqOnCmd, name: "INTEN_CMD")
                    .WithTag("INTEN_SSEND", 5, 1)
                },

                {(long)Registers.CommandRegister, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read,
                        writeCallback: (_, val) =>
                        {
                            if(val)
                            {
                                while(transmitBuffer.Count < (frameSize.Value * frameCounter.Value))
                                {
                                    transmitBuffer.Enqueue(0);
                                }
                            }
                        }, name: "AUTOFILL")
                    .WithTag("AUTOEMPTY", 1, 1)
                    .WithFlag(2, FieldMode.WriteOneToClear | FieldMode.Read, changeCallback: (_, __) => receiveBuffer.Clear(), name: "RXFIFORST")
                    .WithFlag(3, FieldMode.WriteOneToClear | FieldMode.Read, changeCallback: (_, __) => transmitBuffer.Clear(), name: "TXFIFORST")
                    .WithFlag(4, FieldMode.WriteOneToClear | FieldMode.Read,
                        changeCallback: (_, __) =>
                        {
                            frameCounter.Value = 0;
                            framesReceived = 0;
                            framesTransmitted = 0;
                        }, name: "CLRFRAMECNT")
                    .WithTag("AUTOSTALL", 5, 1)
                    .WithTag("TXNOW", 6, 1)
                },

                {(long)Registers.CommandSize, new DoubleWordRegister(this)
                    .WithValueField(0, 6, out commandSize, name: "CMDSIZE")
                },

                {(long)Registers.Status8, new DoubleWordRegister(this)
                    .WithTag("FIRSTFRAME", 0, 1)
                    .WithFlag(1, FieldMode.Read, valueProviderCallback: (_) => dataSent.Value && dataReceived.Value, name: "DONE")
                    .WithFlag(2, FieldMode.Read, valueProviderCallback: (_) => receiveBuffer.Count == 0, name: "RXEMPTY")
                    .WithFlag(3, FieldMode.Read, valueProviderCallback: (_) => transmitBuffer.Count == frameSize.Value, name: "TXFULL")
                    .WithFlag(4, FieldMode.Read, valueProviderCallback: (_) => receiveOverflow.Value, name: "RXOVFLOW")
                    .WithFlag(5, FieldMode.Read, valueProviderCallback: (_) => transmitUnderrun.Value, name: "TXUNDERRUN")
                    .WithTag("SSEL", 6, 1)
                    .WithTag("ACTIVE", 7, 1)
                },

                {(long)Registers.AliasedControlRegister0, new DoubleWordRegister(this)
                    .WithValueField(0, 8,
                        writeCallback: (_, newVal) => controlRegister.Write(0, BitHelper.SetBitsFrom(controlRegister.Value, newVal, 0, 8)),
                        valueProviderCallback: (_) => BitHelper.GetMaskedValue(controlRegister.Value, 0, 8), name: "CTRL0")
                },

                {(long)Registers.AliasedControlRegister1, new DoubleWordRegister(this)
                    .WithValueField(0, 8,
                        writeCallback: (_, newVal) => controlRegister.Write(0, BitHelper.SetBitsFrom(controlRegister.Value, newVal, 8, 8)),
                        valueProviderCallback: (_) => BitHelper.GetMaskedValue(controlRegister.Value, 8, 8), name: "CTRL1")
                },

                {(long)Registers.AliasedControlRegister2, new DoubleWordRegister(this)
                    .WithValueField(0, 8,
                        writeCallback: (_, newVal) => controlRegister.Write(0, BitHelper.SetBitsFrom(controlRegister.Value, newVal, 16, 8)),
                        valueProviderCallback: (_) => BitHelper.GetMaskedValue(controlRegister.Value, 16, 8), name: "CTRL2")
                },

                {(long)Registers.AliasedControlRegister3, new DoubleWordRegister(this)
                    .WithValueField(0, 8,
                        writeCallback: (_, newVal) => controlRegister.Write(0, BitHelper.SetBitsFrom(controlRegister.Value, newVal, 24, 8)),
                        valueProviderCallback: (_) => BitHelper.GetMaskedValue(controlRegister.Value, 24, 8), name: "CTRL3")
                }
            };

            registers = new DoubleWordRegisterCollection(this, registersMap);
        }

        public override void Reset()
        {
            fifoSize = 0;
            framesReceived = 0;
            framesTransmitted = 0;

            receiveBuffer.Clear();
            transmitBuffer.Clear();

            registers.Reset();
            RefreshInterrupt();
        }

        public uint ReadDoubleWord(long offset)
        {
            var result = 0u;
            lock(locker)
            {
                result = registers.Read(offset);
            }
            RefreshInterrupt();
            return result;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            lock(locker)
            {
                registers.Write(offset, value);
            }
            RefreshInterrupt();
        }

        public byte Transmit(byte data)
        {
            var returnValue = (byte)0x00;
            lock(locker)
            {
                if (master.Value)
                {
                    this.Log(LogLevel.Warning, "Cannot receive data when in master mode.");
                    return returnValue;
                }
                if (!coreEnabled.Value)
                {
                    this.Log(LogLevel.Warning, "Cannot receive due to inactive core.");
                    return returnValue;
                }
                TryReceive(data);
                if (framesReceived == commandSize.Value)
                {
                    fullCommandReceived.Value = true;
                }
                if (transmitBuffer.Count > 0)
                {
                    returnValue = transmitBuffer.Dequeue();
                }
                else
                {
                    transmitUnderrun.Value = true;
                }
            }
            RefreshInterrupt();
            return returnValue;
        }

        public void FinishTransmission()
        {
        }

        public long Size => 0x1000;

        public GPIO IRQ { get; }

        private void TryReceive(byte data)
        {
            if (receiveBuffer.Count < (fifoSize * frameSize.Value))
            {
                receiveBuffer.Enqueue(data);
                // Docs are not clear if this should be done in slave or master mode.
                if(!disableFrameCount.Value && receiveBuffer.Count % frameSize.Value == 0)
                {
                    framesReceived++;
                    if (framesReceived == frameCounter.Value)
                    {
                        dataReceived.Value = true;
                        receiveDone.Value = true;
                    }
                }
            }
            else
            {
                receiveOverflow.Value = true;
            }
        }

        private void RefreshInterrupt()
        {
            IRQ.Set(maskedInterruptRegister.Value != 0);
        }

        private readonly DoubleWordRegisterCollection registers;
        private readonly DoubleWordRegister controlRegister;
        private readonly DoubleWordRegister maskedInterruptRegister;
        private readonly Queue<byte> receiveBuffer;
        private readonly Queue<byte> transmitBuffer;
        private IFlagRegisterField coreEnabled;
        private IFlagRegisterField master;
        private IFlagRegisterField enableIrqOnReceive;
        private IFlagRegisterField enableIrqOnTransmit;
        private IFlagRegisterField enableIrqOnOverflow;
        private IFlagRegisterField enableIrqOnUnderrun;
        private IValueRegisterField frameCounter;
        private IValueRegisterField frameSize;
        private IFlagRegisterField dataSent;
        private IFlagRegisterField dataReceived;
        private IFlagRegisterField receiveOverflow;
        private IFlagRegisterField transmitUnderrun;
        private IFlagRegisterField fullCommandReceived;
        private IFlagRegisterField transmitDone;
        private IFlagRegisterField receiveDone;
        private IFlagRegisterField disableFrameCount;
        private IFlagRegisterField enableIrqOnCmd;
        private IValueRegisterField commandSize;
        private int fifoSize;
        private int framesReceived;
        private int framesTransmitted;
        private object locker;

        private enum Registers : long
        {
            Control = 0x00,
            FrameSize = 0x04,
            Status = 0x08,
            InterruptClear = 0x0c,
            ReceiveData = 0x10,
            TransmitData = 0x14,
            ClockRate = 0x18,
            SlaveSelect = 0x1c,
            InterruptMasked = 0x20,
            InterruptRaw = 0x24,
            ControlBitsForEnhancedModes = 0x28,
            CommandRegister = 0x2c,
            PacketSize = 0x30,
            CommandSize = 0x34,
            SlaveHardwareStatus = 0x38,
            Status8 = 0x3c,
            AliasedControlRegister0 = 0x40,
            AliasedControlRegister1 = 0x44,
            AliasedControlRegister2 = 0x48,
            AliasedControlRegister3 = 0x4c
        }
    }
}
