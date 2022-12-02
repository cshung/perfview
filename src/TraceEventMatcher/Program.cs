namespace TraceEventMatcher
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Xml.Linq;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;

    public static class Program
    {
        /**
         * This is an experimental prototype program that attempts to reconcile the difference between the set of events the runtime produce and
         * the set of events TraceEvent can parse. The goal is to get to a point where this tool can detect differences and drive progress.
         */
        private static void Main(string[] args)
        {
            string[] source1 = GetPerfViewParsedEvents().OrderBy(t => t).Select(t => t.ToLower()).Distinct().ToArray();
            string[] source2 = GetManifestParsedEvents().OrderBy(t => t).Select(t => t.ToLower()).Distinct().ToArray();
            Array.Sort(source1);
            Array.Sort(source2);
            File.WriteAllLines(@"c:\garbage\test1.txt", source1);
            File.WriteAllLines(@"c:\garbage\test2.txt", source2);
        }

        private static IEnumerable<string> GetManifestParsedEvents()
        {
            using (var file = File.OpenRead(@"C:\dev\runtime\src\coreclr\vm\ClrEtwAll.man"))
            {
                XElement manifest = XElement.Load(file);
                string ns = manifest.GetDefaultNamespace().NamespaceName;
                foreach (XElement provider in manifest.Element(XName.Get("instrumentation", ns)).Elements(XName.Get("events", ns)).Elements())
                {
                    string providerName = provider.Attribute("name").Value;
                    if (providerName == "Microsoft-Windows-DotNETRuntime")
                    {
                        foreach (XElement templates in provider.Elements(XName.Get("templates", ns)))
                        {
                            foreach (XElement template in templates.Elements(XName.Get("template", ns)))
                            {
                                yield return TrimVersion(template.Attribute("tid").Value);
                            }
                        }
                    }
                }
            }
            yield break;
        }

        private static string TrimVersion(string value)
        {
            return value;
        }

        /**
         * This code assumes a certain pattern that we use in the ClrTraceEventParser.cs
         */
        private static IEnumerable<string> GetPerfViewParsedEvents()
        {
            string perfViewFile = File.ReadAllText(@"C:\dev\perfview\src\TraceEvent\Parsers\ClrTraceEventParser.cs");
            string[] lines = perfViewFile.Split("\n");
            StringBuilder sb = new StringBuilder();
            Regex regex = new Regex(@"Version == (\d*)");

            foreach (string line in lines)
            {
                // If we have a line in ClrTraceEventParser.cs that contains both class an TraceData, we assume we are defining 
                // the TraceData class
                if (line.Contains("class") && line.Contains("TraceData"))
                {
                    string[] names = line.Split(" ");
                    foreach (string name in names)
                    {
                        if (name.EndsWith("TraceData"))
                        {
                            int start = perfViewFile.IndexOf(line);
                            int i = start;
                            int b = 0;
                            sb.Clear();
                            // Here we use a simple state machine to capture the content of the class.
                            while (true)
                            {
                                char c = perfViewFile[i];
                                sb.Append(c);
                                if (c == '{')
                                {
                                    b++;
                                }
                                else if (c == '}')
                                {
                                    b--;
                                    if (b == 0)
                                    {
                                        break;
                                    }
                                }
                                i++;
                            }

                            // Inside the class, there should be some validation code that check against version number
                            // using a regex to capture that.
                            MatchCollection ms = regex.Matches(sb.ToString());
                            foreach (Match m in ms)
                            {
                                yield return (name.Substring(0, name.Length - "TraceData".Length) + "_v" + m.Groups[1].Value).Replace("_v0", "");
                            }
                        }
                    }
                }
            }
        }
    }
}