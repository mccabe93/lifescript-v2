using LifeScript.Viewer;

string path = args.Length > 0 ? args[0] : Path.Combine("samples", "conway.ls");
if (!File.Exists(path))
{
    Console.Error.WriteLine($"{path} not found. Usage: LifeScript.Viewer [script.ls]");
    return 1;
}
new ViewerApp(path).Run();
return 0;
