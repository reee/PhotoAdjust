void DumpType(System.Type t, string[] filters)
{
    Console.WriteLine("== " + t.Name);
    foreach (var f in filters)
    {
        foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            if (m.Name.Contains(f, System.StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("  " + m.MemberType + " " + m);
    }
}
DumpType(typeof(SkiaSharp.SKCodec), new[] { "Create", "Scaled", "Info", "Decode" });
DumpType(typeof(SkiaSharp.SKBitmap), new[] { "GetPixels", "RowBytes", "Alloc", "Width", "Height" });
DumpType(typeof(SkiaSharp.SKImageInfo), new[] { "ctor" });
DumpType(typeof(SkiaSharp.SKSamplingOptions), new[] { "Quality", "Linear" });
DumpType(typeof(SkiaSharp.SKColorType), new[] { "Rgba", "Rgb" });
