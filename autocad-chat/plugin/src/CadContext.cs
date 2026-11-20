using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADChat
{
    /// <summary>What the panel tells Claude about the drawing with every message:
    /// the active drawing, units, current layer and space, and the selection.</summary>
    public static class CadContext
    {
        private static readonly string[] UnitNames =
        {
            "unitless", "inches", "feet", "miles", "millimeters", "centimeters", "meters", "kilometers",
            "microinches", "mils", "yards", "angstroms", "nanometers", "microns", "decimeters",
            "decameters", "hectometers", "gigameters", "astronomical units", "light years", "parsecs",
        };

        public static Dictionary<string, object?> Capture()
        {
            var ctx = new Dictionary<string, object?>();
            Document? doc = null;
            try { doc = AcApp.DocumentManager.MdiActiveDocument; } catch (Exception) { }
            if (doc == null) return ctx;

            try
            {
                var name = Sys("DWGNAME") as string ?? doc.Name;
                ctx["doc_name"] = name;
                var titled = Convert.ToInt32(Sys("DWGTITLED") ?? 0) == 1;
                ctx["doc_path"] = titled ? doc.Name : null;
                var ins = Convert.ToInt32(Sys("INSUNITS") ?? 0);
                ctx["units"] = ins >= 0 && ins < UnitNames.Length ? UnitNames[ins] : $"INSUNITS {ins}";
                ctx["layer"] = Sys("CLAYER") as string;
                var tile = Convert.ToInt32(Sys("TILEMODE") ?? 1);
                ctx["space"] = tile == 1 ? "model space" : "paper space layout " + (Sys("CTAB") as string ?? "");
            }
            catch (Exception ex)
            {
                Log.Write("context: " + ex.Message);
            }

            try
            {
                // The pickfirst set: what the user has selected (grips showing) right now.
                PromptSelectionResult sel = doc.Editor.SelectImplied();
                if (sel.Status == PromptStatus.OK && sel.Value != null)
                {
                    var ids = sel.Value.GetObjectIds();
                    var handles = new List<string>();
                    foreach (ObjectId id in ids)
                    {
                        if (handles.Count >= 200) break;
                        handles.Add(id.Handle.ToString());
                    }
                    ctx["selection"] = handles;
                    ctx["selection_count"] = ids.Length;
                }
            }
            catch (Exception ex)
            {
                Log.Write("selection: " + ex.Message);
            }
            return ctx;
        }

        public static bool IsDarkTheme()
        {
            try { return Convert.ToInt32(Sys("COLORTHEME") ?? 0) == 0; }
            catch (Exception) { return true; }
        }

        private static object? Sys(string name)
        {
            try { return AcApp.GetSystemVariable(name); }
            catch (Exception) { return null; }
        }
    }
}
