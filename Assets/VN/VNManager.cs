using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using RTSEngine.Scene;

namespace Game.VN {
    public class VNManager : MonoBehaviour {
        [SerializeField] private TextAsset scriptFile;
        [SerializeField] private Image characterImage;
        [SerializeField] private Image backgroundImage;
        [SerializeField] private Text dialogueText;
        [SerializeField] private Transform choicesParent;
        [SerializeField] private GameObject choiceButtonPrefab;
        [SerializeField] private string characterImagePath = "VN/Characters";
        [SerializeField] private SceneLoader sceneLoader = new SceneLoader();

        private readonly List<VNInstruction> instructions = new();
        private readonly Dictionary<string, int> labelMap = new();
        private readonly Stack<int> callStack = new();
        private int index;

        private void Start() {
            if (scriptFile != null)
                RenPyParser.Parse(scriptFile.text, instructions, labelMap);
            ExecuteCurrent();
        }
        private void ExecuteCurrent() {
            if (index < 0 || index >= instructions.Count)
                return;

            var cmd = instructions[index];
            switch (cmd) {
                case Dialogue d:
                    ShowDialogue(d);
                    break;
                case MenuCommand m:
                    ShowMenu(m);
                    break;
                case Jump j:
                    JumpTo(j.label);
                    break;
                case Call c:
                    callStack.Push(index + 1);
                    JumpTo(c.label);
                    break;
                case Return:
                    if (callStack.Count > 0) {
                        index = callStack.Pop();
                        ExecuteCurrent();
                    }
                    break;
                case ShowCharacter s:
                    LoadCharacter(s.name, s.expression);
                    index++;
                    ExecuteCurrent();
                    break;
                case SceneCommand sc:
                    LoadBackground(sc.background);
                    index++;
                    ExecuteCurrent();
                    break;
                default:
                    index++;
                    ExecuteCurrent();
                    break;
            }
        }
        private void JumpTo(string label) {
            if (labelMap.TryGetValue(label, out var idx)) {
                index = idx;
                ExecuteCurrent();
            }
        }

        private void ShowDialogue(Dialogue d) {
            foreach (Transform child in choicesParent)
                Destroy(child.gameObject);

            if (dialogueText)
                dialogueText.text = string.IsNullOrEmpty(d.character)
                    ? d.text
                    : $"{d.character}: {d.text}";

            var btn = Instantiate(choiceButtonPrefab, choicesParent);
            if (btn.TryGetComponent(out Text txt))
                txt.text = "Next";
            btn.GetComponent<Button>().onClick.AddListener(() => { index++; ExecuteCurrent(); });
        }
        private void ShowMenu(MenuCommand m) {
            foreach (Transform child in choicesParent)
                Destroy(child.gameObject);
            if (dialogueText)
                dialogueText.text = string.Empty;

            foreach (var choice in m.choices) {
                var btn = Instantiate(choiceButtonPrefab, choicesParent);
                if (btn.TryGetComponent(out Text txt))
                    txt.text = choice.text;
                btn.GetComponent<Button>().onClick.AddListener(() => { JumpTo(choice.targetLabel); });
            }
        }
        private void LoadCharacter(string name, string expr) {
            if (!characterImage)
                return;
            string basePath = characterImagePath.TrimEnd('/');
            string path = string.IsNullOrEmpty(expr) ? $"{basePath}/{name}" : $"{basePath}/{name}_{expr}";
            Sprite sprite = Resources.Load<Sprite>(path);
            if (!sprite)
                sprite = Resources.Load<Sprite>($"{basePath}/{name}");
            characterImage.sprite = sprite;
        }
        private void LoadBackground(string bg) {
            if (!backgroundImage)
                return;

            string path = $"VN/Backgrounds/{bg}";
            Sprite sprite = Resources.Load<Sprite>(path);
            backgroundImage.sprite = sprite;
        }
    }
    #region Parser and Commands
    public abstract class VNInstruction { }

    public class Dialogue : VNInstruction {
        public string character;
        public string text;
    }

    public class MenuChoice {
        public string text;
        public string targetLabel;
    }

    public class MenuCommand : VNInstruction {
        public List<MenuChoice> choices = new();
    }

    public class Jump : VNInstruction { public string label; }
    public class Call : VNInstruction { public string label; }
    public class Return : VNInstruction { }
    public class ShowCharacter : VNInstruction { public string name; public string expression; }
    public class SceneCommand : VNInstruction { public string background; }

    public static class RenPyParser {
        public static void Parse(string script, List<VNInstruction> list, Dictionary<string, int> labels) {
            string[] lines = script.Split('\n');
            for (int i = 0; i < lines.Length; i++) {
                string raw = lines[i];
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                if (line.StartsWith("label ")) {
                    string label = line.Substring(6).TrimEnd(':').Trim();
                    labels[label] = list.Count;
                } else if (line.StartsWith("jump ")) {
                    list.Add(new Jump { label = line.Substring(5).Trim() });
                } else if (line.StartsWith("call ")) {
                    string lbl = line.Substring(5).Trim().Split(' ')[0];
                    list.Add(new Call { label = lbl });
                } else if (line == "return") {
                    list.Add(new Return());
                } else if (line.StartsWith("menu:")) {
                    var menu = new MenuCommand();
                    i++;
                    while (i < lines.Length) {
                        string ml = lines[i];
                        if (!ml.StartsWith("    ")) { i--; break; }
                        ml = ml.Trim();
                        if (ml.StartsWith("\"")) {
                            int end = ml.IndexOf("\"", 1);
                            string text = ml.Substring(1, end - 1);
                            i++;
                            string action = lines[i].Trim();
                            string target = action.StartsWith("jump ") ? action.Substring(5).Trim() :
                                            action.StartsWith("call ") ? action.Substring(5).Trim().Split(' ')[0] : string.Empty;
                            menu.choices.Add(new MenuChoice { text = text, targetLabel = target });
                        }
                        i++;
                    }
                    list.Add(menu);
                } else if (line.StartsWith("scene ")) {
                    string bg = line.Substring(6).Split(' ')[0].Trim();
                    list.Add(new SceneCommand { background = bg });
                } else if (line.StartsWith("show ")) {
                    string[] parts = line.Substring(5).Split(' ');
                    string name = parts[0];
                    string expr = parts.Length > 1 ? parts[1] : string.Empty;
                    list.Add(new ShowCharacter { name = name, expression = expr });
                } else if (line.StartsWith("\"") || line.StartsWith("'")) {
                    string text = ExtractQuoted(line);
                    list.Add(new Dialogue { character = null, text = text });
                } else if (line.Contains("\"") && line.IndexOf('"') > 0) {
                    int firstQuote = line.IndexOf('"');
                    string character = line.Substring(0, firstQuote).Trim();
                    string text = ExtractQuoted(line.Substring(firstQuote));
                    list.Add(new Dialogue { character = character.Trim(' ', ':'), text = text });
                }
            }
        }

        private static string ExtractQuoted(string line) {
            int first = line.IndexOf('"');
            int last = line.LastIndexOf('"');
            if (first >= 0 && last > first)
                return line.Substring(first + 1, last - first - 1);
            return line;
        }
    }
    #endregion
}
