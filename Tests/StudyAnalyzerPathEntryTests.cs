#if STUDY_ANALYZER_UI_TEST
using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using AvatarStroop.Apps;

namespace AvatarStroop.Tests
{
    // No form is shown and no participant file is opened. Exercise the actual
    // WinForms textbox and add handler, not the OS file picker's legacy limit.
    public static class StudyAnalyzerPathEntryTests
    {
        static int passed,failed;
        static void Assert(bool value){if(!value)throw new Exception("Assertion failed");}
        static void Test(string name,Action action)
        {try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+" / "+e);}}
        static T Field<T>(AnalyzerForm form,string name){return (T)typeof(AnalyzerForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);}
        static void Add(AnalyzerForm form,string value)
        {
            Field<TextBox>(form,"pathEntry").Text=value;
            typeof(AnalyzerForm).GetMethod("AddManualPath",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
        }
        [STAThread]
        public static int Main()
        {
            string longPath=Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory),"synthetic",new string('a',120),new string('b',120),"participant.csv");
            Test("manual GUI path entry retains quoted paths beyond MAX_PATH",()=>{
                using(var form=new AnalyzerForm())
                {
                    var text=Field<TextBox>(form,"pathEntry");text.CreateControl();Assert(text.MaxLength>=32767);
                    string quoted="\""+longPath+"\"";text.Text=quoted;Assert(text.Text==quoted&&text.Text.Length>260);
                    Add(form,quoted);var selected=Field<ListBox>(form,"inputs");
                    Assert(selected.Items.Count==1&&(string)selected.Items[0]==longPath);Assert(text.Text.Length==0);
                }
            });
            Test("manual GUI path entry deduplicates while preserving original filename",()=>{
                using(var form=new AnalyzerForm())
                {Add(form,longPath);Add(form,longPath.ToUpperInvariant());var selected=Field<ListBox>(form,"inputs");Assert(selected.Items.Count==1&&(string)selected.Items[0]==longPath);}
            });
            Test("invalid path entry remains visible without adding or analyzing a file",()=>{
                using(var form=new AnalyzerForm())
                {Add(form," ");Assert(Field<ListBox>(form,"inputs").Items.Count==0);Add(form,"unsupported.txt");Assert(Field<ListBox>(form,"inputs").Items.Count==0&&Field<TextBox>(form,"pathEntry").Text=="unsupported.txt");}
            });
            Console.WriteLine("STUDY ANALYZER GUI RESULT "+passed+" passed; "+failed+" failed");return failed==0?0:1;
        }
    }
}
#endif
