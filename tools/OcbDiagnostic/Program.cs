using System;
using System.IO;
using System.Linq;
using System.Reflection;

class OcbDiagnostic5
{
    static void Main(string[] args)
    {
        var gameDir = @"C:\Users\darab\WebstormProjects\KitsuneCommand\src\KitsuneCommand\7dtd-binaries";

        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            var c = Path.Combine(gameDir, name);
            if (File.Exists(c)) try { return Assembly.LoadFrom(c); } catch { }
            return null;
        };

        var asm = Assembly.LoadFrom(Path.Combine(gameDir, "Assembly-CSharp.dll"));
        Type[] allTypes;
        try { allTypes = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { allTypes = ex.Types.Where(t => t != null).ToArray(); }

        // Find BlockTexturesFromXML
        var t = allTypes.FirstOrDefault(x => x.Name == "BlockTexturesFromXML");
        if (t == null) { Console.WriteLine("BlockTexturesFromXML not found"); return; }

        Console.WriteLine($"=== BlockTexturesFromXML ===\n");
        Console.WriteLine("Fields:");
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        {
            try { Console.WriteLine($"  [{(f.IsStatic?"static":"inst  ")}] {f.FieldType.Name,-20} {f.Name}"); }
            catch { }
        }

        Console.WriteLine("\nMethods:");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            try
            {
                var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Console.Write($"  {m.ReturnType.Name} {m.Name}({parms})");
                var body = m.GetMethodBody();
                if (body != null)
                    Console.Write($"  [{body.GetILAsByteArray().Length}b IL: {BitConverter.ToString(body.GetILAsByteArray())}]");
                Console.WriteLine();
            }
            catch (Exception ex) { Console.WriteLine($"  ERR: {ex.Message}"); }
        }

        // Also find BlockTextureData to understand the structure
        Console.WriteLine("\n=== BlockTextureData ===\n");
        var btd = allTypes.FirstOrDefault(x => x.Name == "BlockTextureData");
        if (btd != null)
        {
            Console.WriteLine("Fields:");
            foreach (var f in btd.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                try
                {
                    var note = f.FieldType == typeof(byte) ? " <<< BYTE" :
                               f.FieldType == typeof(ushort) ? " <<< USHORT" :
                               f.FieldType == typeof(int) ? " (int)" : "";
                    Console.WriteLine($"  {f.FieldType.Name,-20} {f.Name}{note}");
                }
                catch { }
            }
            Console.WriteLine("\nMethods:");
            foreach (var m in btd.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                try
                {
                    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Console.Write($"  {m.ReturnType.Name} {m.Name}({parms})");
                    var body = m.GetMethodBody();
                    if (body != null)
                        Console.Write($"  [{body.GetILAsByteArray().Length}b: {BitConverter.ToString(body.GetILAsByteArray())}]");
                    Console.WriteLine();
                }
                catch (Exception ex) { Console.WriteLine($"  ERR: {ex.Message}"); }
            }
        }

        Console.WriteLine("\nDone.");
    }
}
