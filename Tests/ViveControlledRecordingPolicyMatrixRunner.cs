using System;
using System.Reflection;

namespace AvatarStroop.Tests
{
    // Compile this entry point only with mechanically renamed copies of the
    // exact policy source, each carrying its own file-local #define set.
    // No application, SDK, writer, native code or user data is in this binary.
    public static class ViveControlledRecordingPolicyMatrixRunner
    {
        public static readonly string[] Symbols={
            "STROOP_CONTROLLED_RECORDING_DEVELOPMENT","STROOP_XR_PREVIEW",
            "UNITY_5_3_OR_NEWER","UNITY_STANDALONE_WIN","DEVELOPMENT_BUILD",
            "UNITY_EDITOR","UNITY_INCLUDE_TESTS","STROOP_TEST_BUILD",
            "VIVE_RAW_SYNTHETIC_TEST","VIVE_SYNTHETIC_BATCH_FIXTURE"};
        public static int Main()
        {
            int passed=0,failed=0,open=0;var assembly=Assembly.GetExecutingAssembly();
            for(int mask=0;mask<(1<<Symbols.Length);mask++)
            {
                bool expected=(mask&31)==31&&(mask&~31)==0;
                var type=assembly.GetType("AvatarStroop.Runtime.ViveControlledRecordingPolicyVariant"+mask.ToString("D4"),false);
                var property=type==null?null:type.GetProperty("Available",BindingFlags.NonPublic|BindingFlags.Static);
                bool actual=property!=null&&(bool)property.GetValue(null,null);
                bool success=property!=null&&actual==expected;
                if(actual)open++;if(success)passed++;else failed++;
                Console.WriteLine((success?"PASS":"FAIL")+" mask="+mask+" expected="+expected+" actual="+actual);
            }
            Console.WriteLine("RESULT "+passed+" passed; "+failed+" failed; "+open+" enabled configuration(s)");
            return failed==0&&open==1?0:1;
        }
    }
}
