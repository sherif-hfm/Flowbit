using Flowbit.Service.Authoring;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Flowbit.AuthoringExport <repository-root> <output-directory>");
    return 2;
}
try
{
    AuthoringPackageBuilder.Export(args[0], args[1]);
    Console.WriteLine("Exported flowbit-authoring folder and ZIP.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Authoring export failed: " + error.Message);
    return 1;
}
