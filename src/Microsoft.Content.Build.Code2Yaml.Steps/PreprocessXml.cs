﻿namespace Microsoft.Content.Build.Code2Yaml.Steps
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Xml.Linq;
    using System.Xml.XPath;

    using Microsoft.Content.Build.Code2Yaml.Common;
    using Microsoft.Content.Build.Code2Yaml.Constants;
    using Microsoft.Content.Build.Code2Yaml.DataContracts;
    using Microsoft.Content.Build.Code2Yaml.Utility;

    public class PreprocessXml : IStep
    {
        private static readonly Regex IdRegex = new Regex(@"^(namespace|class|struct|enum|interface)([\S\s]+)$", RegexOptions.Compiled);
        private static readonly Regex ToRegularizeTypeRegex = new Regex(@"^(public|protected|private)(?=.*?&lt;.*?&gt;)", RegexOptions.Compiled);
        private static readonly Regex TemplateLeftTagRegex = new Regex(@"(&lt;)\s*", RegexOptions.Compiled);
        private static readonly Regex TemplateRightTagRegex = new Regex(@"\s*(&gt;)", RegexOptions.Compiled);
        private static IReadOnlyList<string> CopyRightCommentCollection = new List<string>
        {
            @"Copyright (c) Microsoft Corporation. All rights reserved. Licensed under the MIT License. See License.txt in the project root for license information.",
            @"Code generated by Microsoft (R) AutoRest Code Generator.",
            @"Copyright Microsoft Corporation",
            @"Licensed under the Apache License, Version 2.0 (the ""License""); you may not use this file except in compliance with the License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0",
            @"Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an ""AS IS"" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.",
            @"The MIT License (MIT) Copyright (c) 2016 Microsoft Corporation",
            @"The MIT License (MIT) Copyright (c) 2017 Microsoft Corporation",
            @"Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the ""Software""), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:",
            @"The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.",
            @"THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.",
        };
        private static IReadOnlyList<string> KindToDeletedCollection = new List<string> { "file", "dir" };

        public string StepName { get { return "Preprocess"; } }

        public async Task RunAsync(BuildContext context)
        {
            var config = context.GetSharedObject(Constants.Config) as ConfigModel;
            if (config == null)
            {
                throw new ApplicationException(string.Format("Key: {0} doesn't exist in build context", Constants.Config));
            }

            string inputPath = StepUtility.GetDoxygenXmlOutputPath(config.OutputPath);
            var processedOutputPath = StepUtility.GetProcessedXmlOutputPath(config.OutputPath);
            if (Directory.Exists(processedOutputPath))
            {
                Directory.Delete(processedOutputPath, recursive: true);
            }
            var dirInfo = Directory.CreateDirectory(processedOutputPath);
   
            // workaround for Doxygen Bug: it generated xml whose encoding is ANSI while the xml meta is encoding='UTF-8'
            // preprocess in string level: fix style for type with template parameter
            Directory.EnumerateFiles(inputPath, "*.xml").AsParallel().ForAll(
                p =>
                {
                    var content = File.ReadAllText(p, Encoding.UTF8);
                    content = TemplateLeftTagRegex.Replace(content, "$1");
                    content = TemplateRightTagRegex.Replace(content, "$1");
                    XDocument doc = XDocument.Parse(content);                   
                    doc.Save(p);
                });           

            // get friendly uid
            var uidMapping = new ConcurrentDictionary<string, string>();
            var compounddefIdMapping = new ConcurrentDictionary<string, string>();
            await Directory.EnumerateFiles(inputPath, "*.xml").ForEachInParallelAsync(
                p =>
                {
                    XDocument doc = XDocument.Load(p);
                    var def = doc.Root.Element("compounddef");
                    var formatedCompoundDefId = string.Empty;
                    if (def != null)
                    {
                        if(KindToDeletedCollection.Contains(def.Attribute("kind").Value))
                        {
                            File.Delete(p);
                            return Task.FromResult(1);
                        }
                        var id = def.Attribute("id").Value;
                        formatedCompoundDefId = def.Element("compoundname").Value.Replace(Constants.NameSpliter, Constants.IdSpliter);
                        uidMapping[id] = formatedCompoundDefId;
                        compounddefIdMapping[id] = formatedCompoundDefId;
                    }
                    foreach (var node in doc.XPathSelectElements("//memberdef[@id]"))
                    {
                        var id = node.Attribute("id").Value;
                        uidMapping[id] = PreprocessMemberUid(node, formatedCompoundDefId);
                    }
                    return Task.FromResult(1);
                });

            // workaround for Doxygen Bug: it generated extra namespace for code `public string namespace(){ return ""; }`.
            // so if we find namespace which has same name with class, remove it from index file and also remove its file.
            string indexFile = Path.Combine(inputPath, Constants.IndexFileName);
            XDocument indexDoc = XDocument.Load(indexFile);
            var duplicateItems = (from ele in indexDoc.Root.Elements("compound")
                                  let uid = (string)ele.Attribute("refid")
                                  group ele by RegularizeUid(uid) into g
                                  let duplicate = g.FirstOrDefault(e => (string)e.Attribute("kind") == "namespace")
                                  where g.Count() > 1 && duplicate != null
                                  select (string)duplicate.Attribute("refid")).ToList();

            // Get duplicate Ids when ignore case
            var results = duplicateItems.Where(id => compounddefIdMapping.ContainsKey(id)).Select(k => compounddefIdMapping.TryRemove(k, out _)).ToList();
            var duplicatedIds = compounddefIdMapping.GroupBy(k => k.Value.ToLower())
                             .Where(g => g.Count() > 1)
                             .Select(kg => kg.Select(kv => kv.Key))
                             .SelectMany(ke => ke).ToList();

            var extendedIdMaping = new ConcurrentDictionary<string, string>();
            await Directory.EnumerateFiles(inputPath, "*.xml").ForEachInParallelAsync(
            p =>
            {
                XDocument doc = XDocument.Load(p);
                if (Path.GetFileName(p) == Constants.IndexFileName)
                {
                    var toBeRemoved = (from item in duplicateItems
                                       select doc.XPathSelectElement($"//compound[@refid='{item}']")).ToList();
                    foreach (var element in toBeRemoved)
                    {
                        element.Remove();
                    }
                }
                else if (duplicateItems.Contains(Path.GetFileNameWithoutExtension(p)))
                {
                    return Task.FromResult(1);
                }
                else
                {
                    // workaround for Doxygen Bug: https://bugzilla.gnome.org/show_bug.cgi?id=710175
                    // so if we find package section func/attrib, first check its type, if it starts with `public` or `protected`, move it to related section
                    var toBeMoved = new Dictionary<string, List<XElement>>();
                    var packageMembers = doc.XPathSelectElements("//memberdef[@prot='package']").ToList();
                    foreach (var member in packageMembers)
                    {
                        string kind = (string)member.Parent.Attribute("kind");
                        var type = member.Element("type");
                        string regulized, access;
                        if (type != null && TryRegularizeReturnType(type.CreateNavigator().InnerXml, out regulized, out access))
                        {
                            if (regulized == string.Empty)
                            {
                                type.Remove();
                            }
                            else
                            {
                                type.ReplaceWith(XElement.Parse($"<type>{regulized}</type>"));
                            }
                            member.Attribute("prot").Value = access;
                            var belongToSection = GetSectionKind(access, kind);
                            List<XElement> elements;
                            if (!toBeMoved.TryGetValue(belongToSection, out elements))
                            {
                                elements = new List<XElement>();
                                toBeMoved[belongToSection] = elements;
                            }
                            elements.Add(member);
                            member.Remove();
                        }
                    }
                    foreach (var pair in toBeMoved)
                    {
                        var section = doc.XPathSelectElement($"//sectiondef[@kind='{pair.Key}']");
                        if (section == null)
                        {
                            section = new XElement("sectiondef", new XAttribute("kind", pair.Key));
                            doc.Root.Element("compounddef").Add(section);
                        }
                        foreach (var c in pair.Value)
                        {
                            section.Add(c);
                        }
                    }
                }
                foreach (var node in doc.XPathSelectElements("//node()[@refid]"))
                {
                    node.Attribute("refid").Value = RegularizeUid(node.Attribute("refid").Value, uidMapping);
                }
                foreach (var node in doc.XPathSelectElements("//node()[@id]"))
                {
                    node.Attribute("id").Value = RegularizeUid(node.Attribute("id").Value, uidMapping);
                }

                // remove copyright comment
                foreach (var node in doc.XPathSelectElements("//para").ToList())
                {
                    if (CopyRightCommentCollection.Contains(node.Value.Trim()))
                    {
                        node.Remove();
                    }
                }

                string fileName = Path.GetFileNameWithoutExtension(p);
                if (compounddefIdMapping.TryGetValue(fileName, out string formatedFileName))
                {
                    formatedFileName = RegularizeUid(formatedFileName);
                    if (duplicatedIds.Contains(fileName))
                    {
                        fileName = string.Format(Constants.RenamedFormat, formatedFileName, TryGetType(fileName));
                        extendedIdMaping[formatedFileName] = fileName;
                    }
                    else
                    {
                        fileName = formatedFileName;
                    }                                                         
                }
                doc.Save(Path.Combine(dirInfo.FullName, fileName + Path.GetExtension(p)));
                return Task.FromResult(1);
            });
            context.SetSharedObject(Constants.ExtendedIdMappings, extendedIdMaping);
        }

        private static string RegularizeUid(string uid, IDictionary<string, string> memberMapping)
        {
            string mapped;
            if (memberMapping.TryGetValue(uid, out mapped))
            {
                return RegularizeUid(mapped);
            }
            return RegularizeUid(uid);
        }

        private static string RegularizeUid(string uid)
        {
            if (uid == null)
            {
                return uid;
            }
            var m = IdRegex.Match(uid);
            if (m.Success)
            {
                uid = m.Groups[2].Value;
            }
            return uid.Replace(Constants.IdSpliter, Constants.Dot);
        }

        private static bool TryRegularizeReturnType(string type, out string regularized, out string access)
        {
            regularized = access = null;
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }
            var match = ToRegularizeTypeRegex.Match(type);
            if (match.Success)
            {
                access = match.Groups[1].Value;
                regularized = type.Replace(match.Groups[0].Value, string.Empty).Trim();
                return true;
            }
            return false;
        }

        private static string TryGetType(string uid)
        {
            var m = IdRegex.Match(uid);
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
            return string.Empty;
        }

        private static string PreprocessMemberUid(XElement memberDef, string formatedParentId)
        {
            StringBuilder builder = new StringBuilder();
            if (string.IsNullOrEmpty(formatedParentId))
            {
                string parentId = memberDef.Ancestors("compounddef").Single().Attribute("id").Value;
                builder.Append(parentId);
            }
            else
            {
                builder.Append(formatedParentId);
            }
            builder.Append(Constants.IdSpliter);
            builder.Append(memberDef.Element("name").Value);
            if (memberDef.Attribute("kind")?.Value == "function")
            {
                builder.Append("(");
                var parameters = memberDef.XPathSelectElements("param/type").ToList();
                if (parameters.Count > 0)
                {
                    builder.Append(parameters[0].Value);
                }
                foreach (var param in parameters.Skip(1))
                {
                    builder.Append("," + param.Value);
                }
                builder.Append(")");
            }
            return builder.ToString();
        }

        private static string GetSectionKind(string access, string kind)
        {
            var splits = kind.Split(new char[] { '-' });
            splits[0] = access;
            return string.Join("-", splits);
        }
    }
}
