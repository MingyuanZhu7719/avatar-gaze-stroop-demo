using System;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Arithmetic only: no file system, writer, participant or device access.
    public static class ViveRawByteBudgetTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW byte budget requires explicit bounded selection",()=>{
                int terminal=ViveRawByteBudget.TerminalFrameBytes;
                Check.True(terminal>40);
                byte[] digest;var payload=ViveRawSessionJournalCodec.TerminalPayloadV3(1,-1,0,0,0,0,0);
                Check.Equal(terminal,ViveRawSessionJournalCodec.FrameV3(payload,new byte[32],false,out digest).Length);
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveRawByteBudget.SelectExplicit(0));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveRawByteBudget.SelectExplicit(terminal));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveRawByteBudget.SelectExplicit(ViveRawByteBudget.MaximumSupportedBytes+1));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveRawByteBudget.SelectExplicit(Int64.MaxValue));
                var budget=ViveRawByteBudget.SelectExplicit(ViveRawByteBudget.MaximumSupportedBytes);
                Check.Equal(8589934592L,budget.MaximumBytes);
            });
            suite.Run("VRAW budget reserves exact terminal space and handles exact data boundary",()=>{
                int terminal=ViveRawByteBudget.TerminalFrameBytes;
                var budget=ViveRawByteBudget.SelectExplicit(terminal+100);
                Check.True(budget.FitsData(0,100));Check.True(!budget.FitsData(0,101));
                Check.True(budget.FitsData(99,1));Check.True(!budget.FitsData(99,2));
                Check.True(!budget.FitsData(100,1));Check.True(budget.FitsTerminal(100,terminal));
                Check.True(!budget.FitsTerminal(101,terminal));Check.True(!budget.FitsTerminal(0,terminal-1));
            });
            suite.Run("VRAW budget rejects invalid counters and avoids signed overflow",()=>{
                var budget=ViveRawByteBudget.SelectExplicit(ViveRawByteBudget.MaximumSupportedBytes);
                Check.True(!budget.FitsData(-1,1)&&!budget.FitsData(0,0)&&!budget.FitsData(0,-1));
                Check.True(!budget.FitsData(Int64.MaxValue,Int32.MaxValue));
                Check.True(!budget.FitsTerminal(Int64.MaxValue,ViveRawByteBudget.TerminalFrameBytes));
                Check.True(!budget.FitsTerminal(-1,ViveRawByteBudget.TerminalFrameBytes));
                Check.True(budget.FitsData(0,Int32.MaxValue));
                var configured=ViveRawByteBudget.SelectExplicit(128L*1024*1024);
                Check.True(configured.FitsData(64L*1024*1024,1024*1024));
                long lastDataByte=configured.MaximumBytes-ViveRawByteBudget.TerminalFrameBytes;
                Check.True(configured.FitsData(lastDataByte-1,1)&&!configured.FitsData(lastDataByte,1));
                Check.True(configured.FitsTerminal(lastDataByte,ViveRawByteBudget.TerminalFrameBytes));
            });
        }
    }
}
