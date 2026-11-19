using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Koma;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

try { await Run(); Console.WriteLine("PASS publication recovery and loopback protocol checks. Synthetic local files only; no public peers, trackers, browser, real queue or account used."); }
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode=1; }

static async Task Run()
{
    string project=Path.Combine(Path.GetTempPath(),"Koma-Torrent-Tests");
    Require(!Directory.Exists(project)||(File.GetAttributes(project)&FileAttributes.ReparsePoint)==0,"Test parent must not be a junction or symlink.");
    string scratch=Path.Combine(project,Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(scratch);File.WriteAllText(Path.Combine(scratch,"synthetic-test.marker"),"Synthetic publication regression profile. Never point at a real profile.");
    string seedDirectory=Directory.CreateDirectory(Path.Combine(scratch,"seed")).FullName;
    byte[] payload=new byte[2*1024*1024+317];new Random(3819).NextBytes(payload);
    string seedFile=Path.Combine(seedDirectory,"Episode 01.mkv");File.WriteAllBytes(seedFile,payload);
    Console.WriteLine("Testing torrent types: "+string.Join(", ",Enum.GetNames<TorrentType>()));
    foreach(var type in Enum.GetValues<TorrentType>())
    {
        var creator=new TorrentCreator(type){Private=false,PieceLength=32768};
        var metadata=await creator.CreateAsync(new TorrentFileSource(seedFile));
        var torrent=Torrent.Load(metadata);byte[] bytes=metadata.Encode();
        Require(torrent.AnnounceUrls.Count==0,"Synthetic torrent must not contain tracker URLs.");
        string caseRoot=Directory.CreateDirectory(Path.Combine(scratch,type.ToString())).FullName;
        int port;var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        using var seedEngine=new ClientEngine(new EngineSettingsBuilder{CacheDirectory=Path.Combine(caseRoot,"seed-cache"),DhtEndPoint=null,
            AllowLocalPeerDiscovery=false,AllowPortForwarding=false,AutoSaveLoadDhtCache=false,AutoSaveLoadFastResume=false,AutoSaveLoadMagnetLinkMetadata=false,
            ListenEndPoints=new(){["ipv4"]=new IPEndPoint(IPAddress.Loopback,port)}}.ToSettings());
        var seed=await seedEngine.AddAsync(torrent,seedDirectory,new TorrentSettingsBuilder{CreateContainingDirectory=false,AllowDht=false,AllowPeerExchange=false}.ToSettings());
        await seed.StartAsync();await Until(()=>seed.State is TorrentState.Seeding or TorrentState.Error,20000,"Seed hash check "+type);
        Require(seed.State==TorrentState.Seeding,"Synthetic seed must validate "+type);
        string library=Path.Combine(caseRoot,"library");
        await using(var download=new TorrentEngine(Path.Combine(caseRoot,"profile"),library,true))
        {
            var job=await download.AddTorrentAsync(bytes);await download.StartAsync(job.Id);
            var manager=Engine(download)!.Torrents.Single();
            await manager.AddPeerAsync(new PeerInfo(new Uri("ipv4://127.0.0.1:"+port)));
            await Until(()=>download.GetSnapshot().Single().State is "Completed" or "Error",40000,"Loopback transfer "+type);
            var finished=download.GetSnapshot().Single();Require(finished.State=="Completed","Loopback transfer failed "+type+": "+finished.Error);
            string final=Path.Combine(finished.Destination,"Episode 01.mkv");Require(File.Exists(final)&&Digest(final)==Digest(seedFile),"Published canonical video must exactly match seed "+type);
            Require(!Directory.EnumerateFiles(finished.Destination,"*.!mt",SearchOption.AllDirectories).Any(),"Publication cannot retain temporary suffixes "+type);
        }
        await seed.StopAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine("PASS loopback transfer and canonical publication "+type);
        // Force the previously timing-dependent suffix state after hashes have passed.
        // This exercises the fallback with known verified bytes, never real files.
        string finalizerRoot=Directory.CreateDirectory(Path.Combine(caseRoot,"forced-stale-suffix")).FullName;
        string forcedComplete=Path.Combine(finalizerRoot,"Episode 01.mkv"),forcedPartial=forcedComplete+".!mt";
        File.Copy(seedFile,forcedPartial);
        using(var verifier=new ClientEngine(new EngineSettingsBuilder{CacheDirectory=Path.Combine(caseRoot,"finalizer-cache"),UsePartialFiles=true,
            DhtEndPoint=null,ListenEndPoints=new(),AllowLocalPeerDiscovery=false,AllowPortForwarding=false,AutoSaveLoadDhtCache=false,
            AutoSaveLoadFastResume=false,AutoSaveLoadMagnetLinkMetadata=false}.ToSettings()))
        {
            var verified=await verifier.AddAsync(torrent,finalizerRoot,new TorrentSettingsBuilder{CreateContainingDirectory=false,AllowDht=false,AllowPeerExchange=false}.ToSettings());
            await verified.HashCheckAsync(false);
            Require(verified.HashChecked&&verified.Complete&&verified.State==TorrentState.Stopped,"Forced suffix test needs fully verified stopped torrent.");
            await Until(()=>File.Exists(forcedComplete)&&!File.Exists(forcedPartial),5000,"Verifier background filename completion");
            await verifier.RemoveAsync(verified);
            File.Move(forcedComplete,forcedPartial);
            typeof(TorrentEngine).GetMethod("FinalizePublicationFiles",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{verified,finalizerRoot});
            Require(File.Exists(forcedComplete)&&!File.Exists(forcedPartial)&&Digest(forcedComplete)==Digest(seedFile),"Explicit finalizer must settle verified stale suffix "+type);
        }
        Console.WriteLine("PASS deterministic stale-suffix finalization "+type);
        foreach(bool shutdown in new[]{false,true})
        {
            string root=Directory.CreateDirectory(Path.Combine(caseRoot,shutdown?"shutdown-repair":"cancel-repair")).FullName;
            string destination=Directory.CreateDirectory(Path.Combine(root,"Published-1234")).FullName;
            string partial=Path.Combine(destination,"Episode 01.mkv.!mt"),canonical=Path.Combine(destination,"Episode 01.mkv");File.Copy(seedFile,partial);
            string profile=Path.Combine(caseRoot,shutdown?"shutdown-profile":"cancel-profile");WriteLegacyQueue(profile,root,destination,bytes,torrent);
            await using(var recovery=new TorrentEngine(profile,root,true))
            {
                using var cancellation=new CancellationTokenSource();Task? closing=null;
                recovery.Changed+=()=>{if(recovery.GetSnapshot().Single().State=="Checking"){if(shutdown)closing??=recovery.ShutdownAsync();else cancellation.Cancel();}};
                try{await Recover(recovery,cancellation.Token);}catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}
                if(closing is not null)await closing.WaitAsync(TimeSpan.FromSeconds(10));
                Require((File.Exists(partial)&&Digest(partial)==Digest(seedFile))||(File.Exists(canonical)&&Digest(canonical)==Digest(seedFile)),"Cancelled/shutdown repair must preserve original content "+type);
                Require(Engine(recovery) is null,"Cancelled offline repair cannot create peer engine "+type);
            }
            await using(var retry=new TorrentEngine(profile,root,true))
            {await Recover(retry);if(retry.GetSnapshot().Single().State!="Completed")await retry.StartAsync(retry.GetSnapshot().Single().Id);
                Require(Engine(retry) is null,"Retrying a published repair must remain offline "+type);
                Require(retry.GetSnapshot().Single().State=="Completed"&&File.Exists(canonical)&&Digest(canonical)==Digest(seedFile),"Interrupted repair must retry safely after restart "+type);}
            Console.WriteLine("PASS interrupted repair and restart "+type+"/"+(shutdown?"shutdown":"cancel"));
        }
        foreach(string scenario in new[]{"valid","corrupt","collision"})
        {
            string root=Directory.CreateDirectory(Path.Combine(caseRoot,"legacy-"+scenario)).FullName;
            string destination=Directory.CreateDirectory(Path.Combine(root,"Published-1234")).FullName;
            string partial=Path.Combine(destination,"Episode 01.mkv.!mt"),complete=Path.Combine(destination,"Episode 01.mkv");
            File.Copy(seedFile,partial);if(scenario=="corrupt"){using var edit=new FileStream(partial,FileMode.Open,FileAccess.Write);edit.WriteByte((byte)(payload[0]^0xff));}
            if(scenario=="collision")File.WriteAllText(complete,"Do not replace this separate user file.");
            string partialHash=Digest(partial),completeHash=File.Exists(complete)?Digest(complete):"";
            string profile=Path.Combine(caseRoot,"legacy-profile-"+scenario);WriteLegacyQueue(profile,root,destination,bytes,torrent);
            await using(var recovery=new TorrentEngine(profile,root,true))
            {
                Require(Engine(recovery) is null,"Reading synthetic queue cannot start peer engine.");
                await Recover(recovery);
                var status=recovery.GetSnapshot().Single();
                Require(Engine(recovery) is null,"Offline repair must not start normal peer engine.");
                if(scenario=="valid")
                {Require(status.State=="Completed"&&File.Exists(complete)&&!File.Exists(partial)&&Digest(complete)==Digest(seedFile),"Valid legacy partial must recover without redownload "+type);}
                else
                {Require(status.State!="Completed"&&File.Exists(partial)&&Digest(partial)==partialHash,"Invalid or colliding legacy partial must remain recoverable "+type+"/"+scenario);
                    if(scenario=="collision")Require(Digest(complete)==completeHash,"Collision destination must not be changed "+type);}
            }
            await using(var restarted=new TorrentEngine(profile,root,true))
            {
                await Recover(restarted);var status=restarted.GetSnapshot().Single();
                Require(status.State==(scenario=="valid"?"Completed":"Error"),"Repair result/retry must persist after restart "+type+"/"+scenario+" got "+status.State);
            }
            Console.WriteLine("PASS offline legacy recovery "+type+"/"+scenario);
        }
        if(torrent.InfoHashes.V2 is not null)
        {
            var noLayers=BEncodedValue.Decode<BEncodedDictionary>(bytes);noLayers.Remove("piece layers");
            var incompleteMetadata=Torrent.Load(noLayers);
            Require(!incompleteMetadata.CreatePieceHashes().HasV2Hashes,"Missing-v2-layer fixture must actually lack hashes.");
            string root=Directory.CreateDirectory(Path.Combine(caseRoot,"missing-layers")).FullName;
            string destination=Directory.CreateDirectory(Path.Combine(root,"Published-1234")).FullName;
            string partial=Path.Combine(destination,"Episode 01.mkv.!mt");File.Copy(seedFile,partial);
            string profile=Path.Combine(caseRoot,"missing-layers-profile");WriteLegacyQueue(profile,root,destination,noLayers.Encode(),incompleteMetadata);
            await using(var recovery=new TorrentEngine(profile,root,true))
            {await Recover(recovery);Require(recovery.GetSnapshot().Single().State=="Error"&&File.Exists(partial)&&Digest(partial)==Digest(seedFile),"Missing v2 hashes must fail closed and preserve original.");}
            Console.WriteLine("PASS missing-v2-layer recovery fails closed "+type);
        }
    }
}
static async Task Recover(TorrentEngine value,CancellationToken token=default)
{
    var method=typeof(TorrentEngine).GetMethod("RecoverCompletedAsync",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)??throw new InvalidOperationException("Root recovery API has not been added yet.");
    object?[] args=method.GetParameters().Select(p=>p.ParameterType==typeof(CancellationToken)?(object)token:p.DefaultValue).ToArray();
    await ((Task)method.Invoke(value,args)!).WaitAsync(TimeSpan.FromSeconds(30));
}
static void WriteLegacyQueue(string profile,string root,string destination,byte[] bytes,Torrent torrent)
{
    Directory.CreateDirectory(Path.Combine(profile,"private"));
    byte[] clear=JsonSerializer.SerializeToUtf8Bytes(new{Version=1,DownloadKiB=0,UploadKiB=1024,Jobs=new[]{new{Id=Guid.NewGuid(),Root=root,Name="Episode 01.mkv",Magnet="",Metadata=bytes,
        Identity=torrent.InfoHashes.V1OrV2.ToHex(),State="Completed",Progress=100d,TotalBytes=torrent.Size,Destination=destination}}});
    try{File.WriteAllBytes(Path.Combine(profile,"private","download-queue.bin"),ProtectedData.Protect(clear,Encoding.UTF8.GetBytes("Koma.DownloadQueue.v1"),DataProtectionScope.CurrentUser));}
    finally{CryptographicOperations.ZeroMemory(clear);}
}
static string Digest(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
static ClientEngine? Engine(TorrentEngine value)=>(ClientEngine?)typeof(TorrentEngine).GetField("engine",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value);
static async Task Until(Func<bool> check,int ms,string label){var end=DateTime.UtcNow.AddMilliseconds(ms);while(!check()){if(DateTime.UtcNow>end)throw new TimeoutException(label);await Task.Delay(80);}}
static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
