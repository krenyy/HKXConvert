using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CommandLine;
using CommandLine.Text;
using HKX2;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace HKXConvert
{
    public class ShouldSerializeContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);

            if (property.PropertyName != null && property.PropertyName.StartsWith("m_"))
                property.PropertyName = property.PropertyName.Substring(2);
            if (property.PropertyName == "Signature") property.ShouldSerialize = instance => false;

            return property;
        }
    }

    public class Program
    {
        private static readonly JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings
        {
            // Formatting = Formatting.Indented,
            TypeNameHandling = TypeNameHandling.Auto,
            ContractResolver = new ShouldSerializeContractResolver(),
            Converters = new List<JsonConverter>
            {
                new StringEnumConverter()
            }
        };

        private static readonly Dictionary<string, short> BotwSectionOffsetForExtension =
            new Dictionary<string, short>
            {
                {"hkcl", 0},
                {"hkrg", 0},
                {"hkrb", 0},
                {"hktmrb", 16},
                {"hknm2", 16}
            };

        internal static string GuessBotwExtension(List<IHavokObject> roots)
        {
            return roots.Count switch
            {
                2 => "hksc",
                1 => ((Func<string>) (() =>
                {
                    return ((hkRootLevelContainer) roots[0]).m_namedVariants[0].m_className switch
                    {
                        "hkpPhysicsData" => "hkrb",
                        "hclClothContainer" => "hkcl",
                        "hkaAnimationContainer" => "hkrg",
                        "hkpRigidBody" => "hktmrb",
                        "hkaiNavMesh" => "hknm2",
                        _ => null
                    };
                }))(),
                _ => null
            };
        }

        internal static IHavokObject ReadHKX(byte[] bytes)
        {
            var des = new PackFileDeserializer();
            var br = new BinaryReaderEx(bytes);

            return des.Deserialize(br);
        }

        internal static byte[] WriteHKX(IHavokObject root, HKXHeader header)
        {
            var s = new PackFileSerializer();
            var ms = new MemoryStream();
            var bw = new BinaryWriterEx(ms);
            s.Serialize(root, bw, header);
            return ms.ToArray();
        }

        internal static List<IHavokObject> ReadBotwHKX(string path)
        {
            return ReadBotwHKX(File.ReadAllBytes(path), path.Split('.').Last());
        }

        internal static List<IHavokObject> ReadBotwHKX(byte[] bytes, string extension)
        {
            if (extension == "hksc")
            {
                var root1 = (StaticCompoundInfo) ReadHKX(bytes);
                var root2 = (hkRootLevelContainer) ReadHKX(bytes.Skip((int) root1.m_Offset).ToArray());

                return new List<IHavokObject> {root1, root2};
            }

            return new List<IHavokObject> {(hkRootLevelContainer) ReadHKX(bytes)};
        }

        public static byte[] WriteBotwHKX(IReadOnlyList<IHavokObject> roots, string extension, HKXHeader header)
        {
            if (extension == "hksc")
            {
                var root1 = (StaticCompoundInfo) roots[0];
                var root2 = (hkRootLevelContainer) roots[1];

                header.SectionOffset = 0;
                var writtenRoot1 = WriteHKX(root1, header);
                root1.m_Offset = (uint) writtenRoot1.Length;
                writtenRoot1 = WriteHKX(root1, header);

                header.SectionOffset = 16;
                var writtenRoot2 = WriteHKX(root2, header);

                var resultBytes = new byte[writtenRoot1.Length + writtenRoot2.Length];
                Buffer.BlockCopy(writtenRoot1, 0, resultBytes, 0, writtenRoot1.Length);
                Buffer.BlockCopy(writtenRoot2, 0, resultBytes, writtenRoot1.Length, writtenRoot2.Length);
                return resultBytes;
            }

            var root = roots[0];
            header.SectionOffset = BotwSectionOffsetForExtension[extension];
            var writtenRoot = WriteHKX(root, header);
            return writtenRoot;
        }

        internal static string CheckIfFileExists(string path)
        {
            if (File.Exists(path)) throw new Exception("File already exists!");

            return path;
        }

        internal static string ChangeFileExtension(string path, string extension)
        {
            var split = path.Split('.');
            path = string.Join('.', split.Take(split.Length - 1).Append(extension));
            return path;
        }

        internal static void HKX2JSON(Options.HKX2JSONOptions options)
        {
            var root = ReadBotwHKX(options.srcFile);
            if (options.prettyprint) jsonSerializerSettings.Formatting = Formatting.Indented;
            var jsonRoot = JsonConvert.SerializeObject(root, jsonSerializerSettings);

            if (options.dstFile is null) options.dstFile = ChangeFileExtension(options.srcFile, "json");

            File.WriteAllText(CheckIfFileExists(options.dstFile), jsonRoot);
        }

        internal static void JSON2HKX(Options.JSON2HKXOptions options)
        {
            var roots = (List<IHavokObject>) JsonConvert.DeserializeObject(
                File.ReadAllText(options.srcFile), typeof(List<IHavokObject>), jsonSerializerSettings);
            var header = options.nx switch
            {
                true => HKXHeader.BotwNx(),
                false => HKXHeader.BotwWiiu()
            };

            var guessedExtension = GuessBotwExtension(roots);

            if (options.dstFile is null) options.dstFile = ChangeFileExtension(options.srcFile, guessedExtension);

            File.WriteAllBytes(CheckIfFileExists(options.dstFile),
                WriteBotwHKX(roots, guessedExtension, header));
        }

        public static void Main(string[] args)
        {
            var parser = new Parser(settings => { settings.IgnoreUnknownArguments = true; });
            var parseResult = parser.ParseArguments<Options.HKX2JSONOptions, Options.JSON2HKXOptions>(args);

            parseResult
                .WithParsed<Options.HKX2JSONOptions>(options => HKX2JSON(options))
                .WithParsed<Options.JSON2HKXOptions>(options => JSON2HKX(options))
                .WithNotParsed(errors => PrintHelpTextOnParseError(parseResult, errors));
        }

        private static void PrintHelpTextOnParseError<T>(ParserResult<T> result, IEnumerable<Error> errors)
        {
            Console.Out.WriteLine(HelpText.AutoBuild(result));
        }

        public class Options
        {
            internal class OptionsBase
            {
                [Value(0, Required = true, HelpText = "Source file.")]
                public string srcFile { get; set; }

                [Value(1, Required = false, HelpText = "Destination file.")]
                public string dstFile { get; set; }
            }

            [Verb("hkx2json", HelpText = "Convert Breath of the Wild Havok packfile to JSON.")]
            internal class HKX2JSONOptions : OptionsBase
            {
                [Option('p', "prettyprint", Required = false, HelpText = "Pretty-print the JSON.")]
                public bool prettyprint { get; set; }
            }

            [Verb("json2hkx", HelpText = "Convert JSON to Breath of the Wild Havok packfile.")]
            internal class JSON2HKXOptions : OptionsBase
            {
                [Option("nx", Required = false,
                    HelpText = "Set output platform to Nintendo Switch. Defaults to Wii U.")]
                public bool nx { get; set; }
            }
        }
    }
}