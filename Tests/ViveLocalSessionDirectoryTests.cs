using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveLocalSessionDirectoryTests
    {
        // This explicit runner passes one fresh, isolated local fixture root.
        // No test accesses historical participant data or deletes a directory.
        public static void Register(TestSuite suite,string root)
        {
            suite.Run("session path proposal is a canonical direct child and performs no filesystem I/O",()=>{
                string missing=Path.Combine(root,"never-created-parent");
                var a=ViveLocalSessionDirectory.Propose(missing+"\\");
                var b=ViveLocalSessionDirectory.Propose(missing);
                Check.Equal(missing,a.ParentPath);
                Check.Equal(missing,Path.GetDirectoryName(a.DirectoryPath));
                Check.True(Regex.IsMatch(Path.GetFileName(a.DirectoryPath),@"^s-\d{8}T\d{6}Z-[a-f0-9]{32}$"));
                Check.True(a.DirectoryPath!=b.DirectoryPath&&!a.Attempted&&!a.Created&&!Directory.Exists(missing));
            });
            suite.Run("two sessions create distinct empty folders without modifying historical outputs",()=>{
                string parent=Directory.CreateDirectory(Path.Combine(root,"populated")).FullName;
                string oldFolder=Directory.CreateDirectory(Path.Combine(parent,"session-old-keep")).FullName;
                string oldRaw=Path.Combine(oldFolder,"recording.vraw"),oldCsv=Path.Combine(parent,"previous.csv");
                byte[] sentinel={1,10,24,255,0};
                File.WriteAllBytes(oldRaw,sentinel);File.WriteAllText(oldCsv,"name,rt\nold,2");
                var a=ViveLocalSessionDirectory.Propose(parent);var b=ViveLocalSessionDirectory.Propose(parent);
                Check.True(!Directory.Exists(a.DirectoryPath)&&!Directory.Exists(b.DirectoryPath));
                string error;
                Check.True(a.TryCreate(out error),error);Check.True(b.TryCreate(out error),error);
                Check.True(a.Attempted&&a.Created&&b.Attempted&&b.Created&&a.DirectoryPath!=b.DirectoryPath);
                Check.True(!Directory.EnumerateFileSystemEntries(a.DirectoryPath).Any()&&!Directory.EnumerateFileSystemEntries(b.DirectoryPath).Any());
                Check.True(!a.TryCreate(out error)&&error.Contains("already been attempted")&&a.Created);
                Check.True(sentinel.SequenceEqual(File.ReadAllBytes(oldRaw)));Check.Equal("name,rt\nold,2",File.ReadAllText(oldCsv));
                Check.Equal(3,Directory.GetDirectories(parent).Length);Check.Equal(1,Directory.GetFiles(parent).Length);
            });
            suite.Run("directory collision never adopts the existing name or silently retries",()=>{
                string parent=Directory.CreateDirectory(Path.Combine(root,"collision")).FullName;
                var a=ViveLocalSessionDirectory.Propose(parent);
                Directory.CreateDirectory(a.DirectoryPath);
                string sentinel=Path.Combine(a.DirectoryPath,"retain.txt");File.WriteAllText(sentinel,"retain everything");
                string error;
                Check.True(!a.TryCreate(out error)&&error.Contains("already uses")&&a.Attempted&&!a.Created);
                Check.True(!a.TryCreate(out error)&&error.Contains("already been attempted"));
                Check.Equal("retain everything",File.ReadAllText(sentinel));Check.Equal(1,Directory.GetDirectories(parent).Length);
            });
            suite.Run("file collision and non-directory parent are refused with bytes preserved",()=>{
                string parent=Directory.CreateDirectory(Path.Combine(root,"file-collision")).FullName;
                var a=ViveLocalSessionDirectory.Propose(parent);
                File.WriteAllText(a.DirectoryPath,"file is not a recording directory");
                string error;
                Check.True(!a.TryCreate(out error)&&a.Attempted&&!a.Created);
                Check.Equal("file is not a recording directory",File.ReadAllText(a.DirectoryPath));
                string shortFileParent=Path.Combine(parent,"existing-file.txt");
                File.WriteAllText(shortFileParent,"an existing file cannot serve as a parent");
                var underFile=ViveLocalSessionDirectory.Propose(shortFileParent);
                Check.True(!underFile.TryCreate(out error)&&error.Contains("parent could not be verified")&&!underFile.Created);
                Check.Equal("an existing file cannot serve as a parent",File.ReadAllText(shortFileParent));
            });
            suite.Run("missing parent is not created and the failed proposal cannot be retried",()=>{
                string parent=Path.Combine(root,"missing");
                var a=ViveLocalSessionDirectory.Propose(parent);string error;
                Check.True(!a.TryCreate(out error)&&error.Contains("No session folder was requested")&&a.Attempted&&!a.Created);
                Check.True(!Directory.Exists(parent));
                Directory.CreateDirectory(parent);
                Check.True(!a.TryCreate(out error)&&!Directory.Exists(a.DirectoryPath));
            });
            suite.Run("simultaneous claims on one proposal have exactly one creation winner",()=>{
                string parent=Directory.CreateDirectory(Path.Combine(root,"concurrent")).FullName;
                var a=ViveLocalSessionDirectory.Propose(parent);int wins=0;
                using(var go=new ManualResetEvent(false))
                {
                    Action claim=()=>{go.WaitOne();string why;if(a.TryCreate(out why))Interlocked.Increment(ref wins);};
                    var x=new Thread(()=>claim());var y=new Thread(()=>claim());
                    x.IsBackground=y.IsBackground=true;x.Start();y.Start();go.Set();
                    Check.True(x.Join(5000)&&y.Join(5000));
                }
                Check.Equal(1,wins);Check.True(a.Attempted&&a.Created);
                Check.Equal(1,Directory.GetDirectories(parent).Length);
            });
            suite.Run("ambiguous, remote, device and overlong paths fail before creation",()=>{
                foreach(string bad in new[]{null,"",@"C:\",@"C:relative",@"\\server\share",@"\\?\C:\folder",
                    @"C:\a\..\b",@"C:\a\.\b",@"C:\a\\b",@"C:\a.\b",@"C:\a \b",
                    @"C:\a:stream",@"C:\NUL\b",@"C:\CON.txt",@"C:/a",@"C:\a\LPT9",
                    "C:\\a\\x"+new string('z',198),"C:\\a\\bad\u0001name"})
                    Check.Throws<ArgumentException>(()=>ViveLocalSessionDirectory.Propose(bad));
            });
            suite.Run("junction/reparse parent is rejected without touching its target",()=>{
                string junction=Path.Combine(root,"redirect"),target=Path.Combine(root,"redirect-target");
                Check.True(Directory.Exists(junction)&&Directory.Exists(target),"runner did not create its synthetic junction");
                Check.True((File.GetAttributes(junction)&FileAttributes.ReparsePoint)!=0,"runner path is not a junction");
                var a=ViveLocalSessionDirectory.Propose(junction);string error;
                Check.True(!a.TryCreate(out error)&&error.Contains("without redirects")&&a.Attempted&&!a.Created);
                Check.True(!Directory.EnumerateFileSystemEntries(target).Any());
            });
        }
    }
}
