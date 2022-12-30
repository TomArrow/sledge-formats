﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Sledge.Formats.Map.Objects;
using Sledge.Formats.Tokens;
using static Sledge.Formats.Tokens.TokenParsing;

namespace Sledge.Formats.Map.Formats
{
    /*  Quake format
     *  {
     *      "classname" "worldspawn"
     *      "key" "value"
     *      "spawnflags" "0"
     *      {
     *          // idTech2:
     *          ( x y z ) ( x y z ) ( x y z ) texturename xshift yshift rotation xscale yscale
     *          // idTech3:
     *          ( x y z ) ( x y z ) ( x y z ) shadername xshift yshift rotation xscale yscale contentflags surfaceflags value
     *          // Worldcraft:
     *          ( x y z ) ( x y z ) ( x y z ) texturename [ ux uy uz xshift ] [ vx vy vz yshift ] rotation xscale yscale
     *      }
     *  }
     *  {
     *      "spawnflags" "0"
     *      "classname" "entityname"
     *      "key" "value"
     *  }
     *  {
     *      "spawnflags" "0"
     *      "classname" "entityname"
     *      "key" "value"
     *      {
     *          ( x y z ) ( x y z ) ( x y z ) texturename xoff yoff rot xscale yscale
     *      }
     *  }
     *  {
     *      patchDef2 // idTech3 ONLY
     *      {
     *          shadername
     *          ( width height 0 0 0 )
     *          (
     *              ( ( x y z u v ) ... ( x y z u v ) )
     *          )
     *          }
     *      }
     *  }
     *  {
     *      brushDef // idTech3 ONLY
     *      {
     *          ( x y z ) ( x y z ) ( x y z ) ( ( ux uy uz ) ( vx vy vz ) ) shadername contentflags surfaceflags value
     *      }
     *  }
     *  {
     *      brushDef3 // idTech4 ONLY
     *      {
     *          ?
     *      }
     *  }
     *  {
     *      patchDef3 // idTech4 ONLY
     *      {
     *          ?
     *      }
     *  }
     */
    public class QuakeMapFormat2 : IMapFormat
    {
        public string Name => "Quake Map";
        public string Description => "The .map file format used for most Quake editors.";
        public string ApplicationName => "Radiant";
        public string Extension => "map";
        public string[] AdditionalExtensions => new[] { "max" };
        public string[] SupportedStyleHints => new[] { "idTech2", "idTech3", "idTech4", "Worldcraft" };

        private static readonly char[] ValidSymbols = {
            Symbols.OpenBracket,    // [
            Symbols.CloseBracket,   // ]
            Symbols.OpenParen,      // (
            Symbols.CloseParen,     // )
            Symbols.OpenBrace,      // {
            Symbols.CloseBrace,     // }
        };

        private static readonly Tokeniser Tokeniser = new Tokeniser(ValidSymbols);

        public MapFile Read(Stream stream)
        {
            var map = new MapFile();
            using (var reader = new StreamReader(stream))
            {
                var tokens = Tokeniser.Tokenise(reader);
                using (var it = tokens.GetEnumerator())
                {
                    it.MoveNext();
                    while (it.Current?.Is(TokenType.Symbol, Symbols.OpenBrace) == true)
                    {
                        var entity = ReadEntity(it);

                        if (entity.ClassName == "worldspawn")
                        {
                            map.Worldspawn.SpawnFlags = entity.SpawnFlags;
                            foreach (var p in entity.Properties) map.Worldspawn.Properties[p.Key] = p.Value;
                            map.Worldspawn.Children.AddRange(entity.Children);
                        }
                        else
                        {
                            map.Worldspawn.Children.Add(entity);
                        }
                    }
                }
            }

            return map;
        }

        #region Read

        private Entity ReadEntity(IEnumerator<Token> it)
        {
            var ent = new Entity();

            Expect(it, TokenType.Symbol, Symbols.OpenBrace);
            while (it.Current?.Is(TokenType.Symbol, Symbols.CloseBrace) == false)
            {
                if (it.Current?.Is(TokenType.String) == true)
                {
                    var key = Expect(it, TokenType.String).Value;
                    var val = Expect(it, TokenType.String).Value;

                    if (key == "classname") ent.ClassName = val;
                    else if (key == "spawnflags") ent.SpawnFlags = int.Parse(val);
                    else ent.Properties[key] = val;
                }
                else if (it.Current?.Is(TokenType.Symbol, Symbols.OpenBrace) == true)
                {
                    var solid = ReadSolid(it);
                    if (solid != null) ent.Children.Add(solid);
                }
                else
                {
                    Debug.Assert(it.Current != null);
                    throw new NotSupportedException($"Parsing error (line {it.Current.Line}, column {it.Current.Column}): Unknown syntax of type {it.Current.Type}: {it.Current.Value}");
                }
            }

            //

            Expect(it, TokenType.Symbol, Symbols.CloseBrace);
            return ent;
        }

        private Solid ReadSolid(IEnumerator<Token> it)
        {
            var s = new Solid();

            Expect(it, TokenType.Symbol, Symbols.OpenBrace);
            while (it.Current?.Is(TokenType.Symbol, Symbols.CloseBrace) == false)
            {
                s.Faces.Add(ReadFace(it));
            }
            Expect(it, TokenType.Symbol, Symbols.CloseBrace);

            s.ComputeVertices();
            return s;
        }

        private Face ReadFace(IEnumerator<Token> it)
        {
            var a = ReadFacePoint(it);
            var b = ReadFacePoint(it);
            var c = ReadFacePoint(it);

            var ab = b - a;
            var ac = c - a;

            var normal = ac.Cross(ab).Normalise();
            var d = normal.Dot(a);

            var face = new Face
            {
                Plane = new Plane(normal, d),
                TextureName = Expect(it, TokenType.Name).Value
            };

            // Worldcraft
            if (it.Current?.Is(TokenType.Symbol, Symbols.OpenBracket) == true)
            {
                (face.UAxis, face.XShift) = ReadTextureAxis(it);
                (face.VAxis, face.YShift) = ReadTextureAxis(it);
                face.Rotation = (float) ParseDecimal(it);
                face.XScale = (float) ParseDecimal(it);
                face.YScale = (float) ParseDecimal(it);
            }
            // idTech2, idTech3
            else
            {
                var direction = ClosestAxisToNormal(face.Plane);
                face.UAxis = direction == Vector3.UnitX ? Vector3.UnitY : Vector3.UnitX;
                face.VAxis = direction == Vector3.UnitZ ? -Vector3.UnitY : -Vector3.UnitZ;

                var numbers = new List<decimal>();
                while (IsNumber()) numbers.Add(ParseDecimal(it));

                if (numbers.Count != 5 && numbers.Count != 8)
                {
                    Debug.Assert(it.Current != null);
                    throw new NotSupportedException($"Parsing error (line {it.Current.Line}, column {it.Current.Column}): Incorrect number of numeric values, expected 5 or 8, got {numbers.Count}.");
                }

                face.XShift = (float) numbers[0];
                face.YShift = (float) numbers[1];
                face.Rotation = (float) numbers[2];
                face.XScale = (float) numbers[3];
                face.YScale = (float) numbers[4];

                if (numbers.Count == 8)
                {
                    // idTech3
                    face.ContentFlags = (int) numbers[5];
                    face.SurfaceFlags = (int) numbers[6];
                    face.Value = (float) numbers[7];
                }

                bool IsNumber()
                {
                    var cur = it.Current;
                    if (cur == null) return false;
                    if (cur.Type == TokenType.Number) return true;
                    if (cur.Type == TokenType.Symbol) return cur.Symbol == Symbols.Plus || cur.Symbol == Symbols.Minus || cur.Symbol == Symbols.Dot;
                    return false;
                }
            }

            return face;
        }

        private Vector3 ReadFacePoint(IEnumerator<Token> it)
        {
            Expect(it, TokenType.Symbol, Symbols.OpenParen);
            var x = (float) ParseDecimal(it);
            var y = (float) ParseDecimal(it);
            var z = (float) ParseDecimal(it);
            Expect(it, TokenType.Symbol, Symbols.CloseParen);

            return new Vector3(x, y, z);
        }

        private (Vector3, float) ReadTextureAxis(IEnumerator<Token> it)
        {
            Expect(it, TokenType.Symbol, Symbols.OpenBracket);
            var x = (float) ParseDecimal(it);
            var y = (float) ParseDecimal(it);
            var z = (float) ParseDecimal(it);
            var sh = (float) ParseDecimal(it);
            Expect(it, TokenType.Symbol, Symbols.CloseBracket);

            return (new Vector3(x, y, z), sh);
        }
        
        private static Vector3 ClosestAxisToNormal(Plane plane)
        {
            var norm = plane.Normal.Absolute();
            if (norm.Z >= norm.X && norm.Z >= norm.Y) return Vector3.UnitZ;
            if (norm.X >= norm.Y) return Vector3.UnitX;
            return Vector3.UnitY;
        }

        #endregion

        public void Write(Stream stream, MapFile map, string styleHint)
        {
            using (var sw = new StreamWriter(stream, Encoding.ASCII, 1024, true))
            {
                WriteWorld(sw, map.Worldspawn, styleHint);
            }
        }

        #region Writing


        private static string FormatVector3(Vector3 c)
        {
            return $"{c.X.ToString("0.000", CultureInfo.InvariantCulture)} {c.Y.ToString("0.000", CultureInfo.InvariantCulture)} {c.Z.ToString("0.000", CultureInfo.InvariantCulture)}";
        }

        private static void CollectNonEntitySolids(List<Solid> solids, MapObject parent)
        {
            foreach (var obj in parent.Children)
            {
                switch (obj)
                {
                    case Solid s:
                        solids.Add(s);
                        break;
                    case Group _:
                        CollectNonEntitySolids(solids, obj);
                        break;
                }
            }
        }

        private static void CollectEntities(List<Entity> entities, MapObject parent)
        {
            foreach (var obj in parent.Children)
            {
                switch (obj)
                {
                    case Entity e:
                        entities.Add(e);
                        break;
                    case Group _:
                        CollectEntities(entities, obj);
                        break;
                }
            }
        }

        private static void WriteFace(StreamWriter sw, Face face, string styleHint)
        {
            // ( -128 64 64 ) ( -64 64 64 ) ( -64 0 64 ) AAATRIGGER [ 1 0 0 0 ] [ 0 -1 0 0 ] 0 1 1
            var strings = face.Vertices.Take(3).Select(x => "( " + FormatVector3(x) + " )").ToList();
            strings.Add(String.IsNullOrWhiteSpace(face.TextureName) ? "NULL" : face.TextureName);
            switch (styleHint)
            {
                case "idTech2":
                    strings.Add("[");
                    strings.Add(face.XShift.ToString("0.000", CultureInfo.InvariantCulture));
                    strings.Add(face.YShift.ToString("0.000", CultureInfo.InvariantCulture));
                    strings.Add(face.Rotation.ToString("0.000", CultureInfo.InvariantCulture));
                    strings.Add(face.XScale.ToString("0.000", CultureInfo.InvariantCulture));
                    strings.Add(face.YScale.ToString("0.000", CultureInfo.InvariantCulture));
                    break;
                case "idTech3":
                    Util.Assert(false, "idTech3 format maps are currently not supported.");
                    break;
                case "idTech4":
                    Util.Assert(false, "idTech4 format maps are currently not supported.");
                    break;
                case "Worldcraft":
                default:
                    strings.Add("[");
                    strings.Add(FormatVector3(face.UAxis));
                    strings.Add(face.XShift.ToString("0.000", CultureInfo.InvariantCulture));
                    strings.Add("]");
                    strings.Add("[");
                    strings.Add(FormatVector3(face.VAxis));
                    strings.Add(face.YShift.ToString("0.000", CultureInfo.InvariantCulture));
                    strings.Add("]");
                    strings.Add(face.Rotation.ToString("0.000", CultureInfo.InvariantCulture));
                    strings.Add(face.XScale.ToString("0.000", CultureInfo.InvariantCulture));
                    strings.Add(face.YScale.ToString("0.000", CultureInfo.InvariantCulture));
                    break;
            }

            sw.WriteLine(String.Join(" ", strings));
        }

        private static void WriteSolid(StreamWriter sw, Solid solid, string styleHint)
        {
            sw.WriteLine("{");
            foreach (var face in solid.Faces)
            {
                WriteFace(sw, face, styleHint);
            }
            sw.WriteLine("}");
        }

        private static void WriteProperty(StreamWriter sw, string key, string value)
        {
            sw.WriteLine('"' + key + "\" \"" + value + '"');
        }

        private static void WriteEntity(StreamWriter sw, Entity ent, string styleHint)
        {
            var solids = new List<Solid>();
            CollectNonEntitySolids(solids, ent);
            WriteEntityWithSolids(sw, ent, solids, styleHint);
        }

        private static void WriteEntityWithSolids(StreamWriter sw, Entity e, IEnumerable<Solid> solids, string styleHint)
        {
            sw.WriteLine("{");

            WriteProperty(sw, "classname", e.ClassName);

            if (e.SpawnFlags != 0)
            {
                WriteProperty(sw, "spawnflags", e.SpawnFlags.ToString(CultureInfo.InvariantCulture));
            }

            foreach (var prop in e.Properties)
            {
                WriteProperty(sw, prop.Key, prop.Value);
            }

            foreach (var s in solids)
            {
                WriteSolid(sw, s, styleHint);
            }

            sw.WriteLine("}");
        }

        private void WriteWorld(StreamWriter sw, Worldspawn world, string styleHint)
        {
            var solids = new List<Solid>();
            var entities = new List<Entity>();

            CollectNonEntitySolids(solids, world);
            CollectEntities(entities, world);

            WriteEntityWithSolids(sw, world, solids, styleHint);

            foreach (var entity in entities)
            {
                WriteEntity(sw, entity, styleHint);
            }
        }

        #endregion
    }
}
