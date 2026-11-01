using System;
using System.Collections.Generic;

namespace NovReader.Services.Legado
{
    /// <summary>精简 JSONPath：支持 $.a.b、$['a']、$[0]、$[-1]、$.a[*]、$..a 与 *。</summary>
    public static class JsonPath
    {
        private sealed class Step
        {
            public char Kind;    // 'P' 属性 'R' 递归 'I' 下标 'A' 全部
            public string Name;
            public int Index;
        }

        public static List<JsonValue> Select(JsonValue root, string path)
        {
            var result = new List<JsonValue>();
            if (root == null) return result;

            if (String.IsNullOrEmpty(path))
            {
                result.Add(root);
                return result;
            }

            var steps = Parse(path);
            if (steps == null || steps.Count == 0)
            {
                result.Add(root);
                return result;
            }

            var current = new List<JsonValue>();
            current.Add(root);
            for (int s = 0; s < steps.Count; s++)
            {
                var next = new List<JsonValue>();
                for (int i = 0; i < current.Count; i++) Apply(current[i], steps[s], next);
                current = next;
                if (current.Count == 0) break;
            }
            return current;
        }

        /// <summary>取首个结果，不存在返回 null。</summary>
        public static JsonValue SelectFirst(JsonValue root, string path)
        {
            var list = Select(root, path);
            return list.Count == 0 ? null : list[0];
        }

        private static void Apply(JsonValue value, Step step, List<JsonValue> output)
        {
            if (value == null) return;

            switch (step.Kind)
            {
                case 'P':
                    if (value.Kind == JsonKind.Object)
                    {
                        var child = value.Get(step.Name);
                        if (child != null) output.Add(child);
                    }
                    break;

                case 'I':
                    if (value.Kind == JsonKind.Array && value.Items != null)
                    {
                        int index = step.Index;
                        if (index < 0) index += value.Items.Count;
                        if (index >= 0 && index < value.Items.Count) output.Add(value.Items[index]);
                    }
                    break;

                case 'A':
                    if (value.Kind == JsonKind.Array && value.Items != null)
                        output.AddRange(value.Items);
                    else if (value.Kind == JsonKind.Object && value.Members != null)
                        for (int i = 0; i < value.Members.Count; i++) output.Add(value.Members[i].Value);
                    break;

                case 'R':
                    foreach (var d in Descendants(value))
                    {
                        if (d == null || d.Kind != JsonKind.Object) continue;
                        var child = d.Get(step.Name);
                        if (child != null) output.Add(child);
                    }
                    break;
            }
        }

        private static IEnumerable<JsonValue> Descendants(JsonValue value)
        {
            if (value == null) yield break;
            yield return value;

            if (value.Kind == JsonKind.Array && value.Items != null)
            {
                for (int i = 0; i < value.Items.Count; i++)
                    foreach (var d in Descendants(value.Items[i])) yield return d;
            }
            else if (value.Kind == JsonKind.Object && value.Members != null)
            {
                for (int i = 0; i < value.Members.Count; i++)
                    foreach (var d in Descendants(value.Members[i].Value)) yield return d;
            }
        }

        private static List<Step> Parse(string path)
        {
            var steps = new List<Step>();
            int i = 0;
            if (i < path.Length && path[i] == '$') i++;

            while (i < path.Length)
            {
                char c = path[i];

                if (c == '.')
                {
                    if (i + 1 < path.Length && path[i + 1] == '.')
                    {
                        i += 2;
                        string name = ReadName(path, ref i);
                        if (name.Length == 0) return null;
                        steps.Add(new Step { Kind = 'R', Name = name });
                        continue;
                    }
                    i++;
                    string n = ReadName(path, ref i);
                    if (n.Length == 0) return null;
                    steps.Add(n == "*" ? new Step { Kind = 'A' } : new Step { Kind = 'P', Name = n });
                    continue;
                }

                if (c == '[')
                {
                    int e = path.IndexOf(']', i);
                    if (e < 0) return null;
                    string body = path.Substring(i + 1, e - i - 1).Trim();
                    i = e + 1;

                    if (body == "*") steps.Add(new Step { Kind = 'A' });
                    else if (body.Length >= 2 && (body[0] == '\'' || body[0] == '"'))
                        steps.Add(new Step { Kind = 'P', Name = body.Trim('\'', '"') });
                    else
                    {
                        int index;
                        if (Int32.TryParse(body, out index)) steps.Add(new Step { Kind = 'I', Index = index });
                        else steps.Add(new Step { Kind = 'P', Name = body });
                    }
                    continue;
                }

                string bare = ReadName(path, ref i);
                if (bare.Length == 0) { i++; continue; }
                steps.Add(bare == "*" ? new Step { Kind = 'A' } : new Step { Kind = 'P', Name = bare });
            }
            return steps;
        }

        private static string ReadName(string path, ref int i)
        {
            int start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[' && path[i] != ']') i++;
            return path.Substring(start, i - start).Trim();
        }
    }
}
