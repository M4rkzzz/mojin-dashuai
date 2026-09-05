using System.Text.Json;

if(args is not ["--update-ready",var nonce]||!Guid.TryParseExact(nonce,"N",out _))return 2;
var directory=Path.GetFullPath(AppContext.BaseDirectory);
if(!directory.Contains(Path.DirectorySeparatorChar+"launcher-update-smoke"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return 3;
if(File.Exists(Path.Combine(directory,"simulate-failure")))return 9;
var root=Directory.GetParent(directory.TrimEnd(Path.DirectorySeparatorChar))!.Parent!.FullName;
var ready=Path.Combine(root,"ready",nonce+".json");
Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
File.WriteAllText(ready+".tmp",JsonSerializer.Serialize(new{processId=Environment.ProcessId,directory}));
File.Move(ready+".tmp",ready);
await Task.Delay(2000);
return 0;
