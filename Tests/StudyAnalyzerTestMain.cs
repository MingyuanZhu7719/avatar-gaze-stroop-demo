#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
namespace AvatarStroop.Tests
{
    public static class StudyAnalyzerTestMain
    {
        public static int Main()
        {
            var suite=new TestSuite();
            StudyDataReaderTests.Register(suite);
            StudyAnalysisTests.Register(suite);
            StudyAnalysisFilesTests.Register(suite);
            Console.WriteLine("STUDY ANALYSIS RESULT "+suite.Passed+" passed; "+suite.Failed+" failed");
            return suite.Failed==0?0:1;
        }
    }
}
#endif
