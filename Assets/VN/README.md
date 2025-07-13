# Visual Novel System

This folder contains the `VNManager` script and a sample story script.

## Creating a VN Scene
1. Create a new Unity scene or open an existing one.
2. Add an empty `GameObject` and attach the `VNManager` component.
3. In the inspector, assign UI references for:
   - `characterImage` (Image to display the current character sprite)
   - `backgroundImage` (Image for scene backgrounds)
   - `dialogueText` (Text element showing dialogue lines)
   - `choicesParent` (Transform that will hold choice buttons)
   - `choiceButtonPrefab` (Button prefab used for the "Next" button and menu choices)
4. Create a `TextAsset` that contains your VN script. See `sample_script.txt` for an example.
5. Drag your script asset into the `scriptFile` field of `VNManager`.
6. Place character sprites in `Resources/VN/Characters` and background images in `Resources/VN/Backgrounds` so they can be loaded by name.

## Running the Story
1. Press **Play** in Unity.
2. The manager parses the script starting from `label start:` and displays the dialogue.
3. Click the **Next** button or any menu choices to progress the story.

## Extending
- Modify `VNManager.cs` if you need additional commands (sound effects, positioning, etc.).
- Scripts follow a light Ren'Py-like syntax as parsed by the manager.

This setup lets you integrate VN sequences between RTS battles and other gameplay elements.
