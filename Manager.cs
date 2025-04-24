using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection.Metadata.Ecma335;
using Godot.NativeInterop;
using System.Dynamic;
using System.Text.Json;

public partial class Manager : Node
{
    [Export]
    public SubViewport Viewport { get; set; }

    [Export]
    public Camera3D Camera { get; set; }

    [Export]
    public ShaderMaterial AlbedoMaterial { get; set; }
    [Export]
    public ShaderMaterial DepthNormalsMaterial { get; set; }
    [Export]
    public ShaderMaterial ORMMaterial { get; set; }
    
    // Called when the node enters the scene tree for the first time
    public override async void _Ready()
    {
        if (Viewport == null || Camera == null)
        {
            GD.PrintErr("Viewport or Camera not assigned to Manager!");
        }

        SceneTree sceneTree = (SceneTree)Engine.GetMainLoop();

        // Disable the main viewport
        // SceneTree sceneTree = (SceneTree)Engine.GetMainLoop();
        // RenderingServer.ViewportSetActive(
        //     sceneTree.Root.GetViewportRid(),
        //     false
        // );

        var args = OS.GetCmdlineUserArgs();
        if (OS.HasFeature("editor")) {
            args = [
                "--model", "C:/Users/Tesse/Downloads/30600.glb",
                "--n_views", "150",
                "--resolution", "1024",
                "--output_path", "res://renders",
                "--lit",
                // "--albedo",
                // "--depth_normals",
            ];
        }

        Dictionary<string, string> config = new();

        string lastKey = null;
        foreach (string arg in args) {
            if (arg.StartsWith('-')) {
                if (lastKey != null) config.Add(lastKey, null);
                if (arg.StartsWith("--")) {
                    lastKey = arg.Substr(2, arg.Length - 2);
                } else {
                    lastKey = arg.Substr(1, arg.Length - 1);
                }
            } else if (lastKey != null) {
                config.Add(lastKey, arg);
                lastKey = null;
            }
        }
        if (lastKey != null) config.Add(lastKey, null);

        string[] requiredFlags = [
            "model", "output_path"
        ];
        bool hasRequiredFlags = requiredFlags.All(config.ContainsKey);

        // Fill in optional values
        Dictionary<string, string> optionalFlagDefaults = new() {
            {"n_views", "150"},
            {"resolution", "512"},
            {"distance", "1.9"},
            {"fov", "50"},
        };
        foreach (var fd in optionalFlagDefaults) {
            if (!config.ContainsKey(fd.Key)) config.Add(fd.Key, fd.Value);
        }

        if (config.ContainsKey("help") || config.ContainsKey("h") || !hasRequiredFlags) {
            GD.Print(@"All3D Godot Render Tool

Flags:
    --help/-h      Prints this message

    (Required)
    --model        [path]    The .glb file to render
    --output_path  [path]    The folder where the renders are saved

    (Optional)
    --n_views      [int]     (150)  Number of views to render
    --resolution   [int]     (512)  The resolution of the renders
    --distance     [float]   (1.9)  The distance of the camera to the origin
    --fov          [float]   (50)   The field of view of the camera in degrees

    (Output types)
    --lit             Render lit mesh
    --albedo          Render albedos
    --orm             Render occlusion/roughness/metallic maps
    --depth_normals   Render depth/normals (alpha channel is depth)
            ");
            sceneTree.Quit();
        }

        if (!hasRequiredFlags) {
            sceneTree.Quit();
            return;
        }

        DirAccess.MakeDirAbsolute(config["output_path"]);

        var res = Int32.Parse(config["resolution"]);
        Viewport.Size = new Vector2I(res, res);

        GD.Print("Loading model...");
        Stopwatch loadSw = new();
        loadSw.Start();
        var modelRoot = await LoadModel(config["model"]);
        // FIXME: Handle null model
        NormalizeModel(modelRoot);
        loadSw.Stop();

        Camera.Fov = float.Parse(config["fov"]);
        var views = FibonacciSphere(Int32.Parse(config["n_views"]), float.Parse(config["distance"]));

        var DoRender = async (string prefix) => {
            GD.Print($"Rendering {prefix}...");
            var taskIds = await this.RenderViews(views, prefix, config["output_path"]);
            GD.Print("Waiting for saving to wrap up...");
            foreach (long id in taskIds) {
                WorkerThreadPool.WaitForTaskCompletion(id);
            }
        };

        Stopwatch renderSw = new();
        GD.Print("Rendering...");
        renderSw.Start();
        if (config.ContainsKey("lit")) {
            await DoRender("Color");
        }
        if (config.ContainsKey("albedo")) {
            this.ApplyMaterialRecursively(modelRoot, AlbedoMaterial);
            await DoRender("Albedo");
        }
        if (config.ContainsKey("depth_normals")) {
            this.ApplyMaterialRecursively(modelRoot, DepthNormalsMaterial);
            await DoRender("DepthNormals");
        }
        if (config.ContainsKey("orm")) {
            this.ApplyMaterialRecursively(modelRoot, ORMMaterial);
            await DoRender("Albedo");
        }
        renderSw.Stop();
        GD.Print("Saving render metadata...");
        List<ExpandoObject> cameraViews = new();
        foreach (var view in views) {
            dynamic viewInfo = new ExpandoObject();

            int resolution = int.Parse(config["resolution"]);
            viewInfo.resolution = resolution;
            viewInfo.depth_range = Camera.Far;

            // Extrinsics
            Camera.GlobalPosition = view;
            Camera.LookAt(Vector3.Zero);
            float[][] extrinsics = Camera.GlobalTransform.AsFloatArray();
            viewInfo.extrinsics = extrinsics;

            // Intrinsics
            // https://photo.stackexchange.com/questions/97213/finding-focal-length-from-image-size-and-fov
            float flPx = (float)resolution/(2f * (float)Mathf.Tan(Mathf.DegToRad(Camera.Fov)/2.0));
            float[][] intrinsics = [
                [flPx, 0, resolution/2],
                [0, flPx, resolution/2],
                [0, 0, 1],
            ];
            viewInfo.intrinsics = intrinsics;

            cameraViews.Add(viewInfo);
        }
        dynamic metadata = new ExpandoObject();
        metadata.version = 2;
        metadata.source = "godot";
        metadata.views = cameraViews;
        var jsonString = JsonSerializer.Serialize(metadata, new JsonSerializerOptions {
            //WriteIndented = true
        });
        //GD.Print(jsonString);
        var fa = Godot.FileAccess.Open(Path.Join(config["output_path"], "metadata.json"), Godot.FileAccess.ModeFlags.Write);
        fa.StoreString(jsonString);
        fa.Close();
        GD.Print($"Done!");
        GD.Print($" - Load elapsed: {loadSw.Elapsed.TotalSeconds}s");
        GD.Print($" - Render elapsed: {renderSw.Elapsed.TotalSeconds}s ({1f/(renderSw.Elapsed.TotalSeconds/150f)}images/s)");
        GD.Print($" - Total elapsed: {loadSw.Elapsed.TotalSeconds + renderSw.Elapsed.TotalSeconds}s");

        sceneTree.Quit();
    }

    private List<Vector3> FibonacciSphere(int samples = 1, float radius = 1.0f)
    {
        List<Vector3> points = new List<Vector3>();
        
        // If we have 0 or negative samples, return empty list
        if (samples <= 0)
            return points;
            
        // Golden angle in radians
        float phi = Mathf.Pi * (3.0f - Mathf.Sqrt(5.0f));
        
        for (int i = 0; i < samples; i++)
        {
            // z goes from 1 to -1
            float z = 1 - (i / (float)(samples - 1)) * 2;
            float radiusAtZ = Mathf.Sqrt(1 - z * z);
            
            // Golden angle increment
            float theta = phi * i;
            
            float x = Mathf.Cos(theta) * radiusAtZ;
            float y = Mathf.Sin(theta) * radiusAtZ;
            
            points.Add(new Vector3(x * radius, y * radius, z * radius));
        }
        
        return points;
    }

    private async Task<Node3D> LoadModel(string path) {
        var gltfDocumentLoad = new GltfDocument();
        var gltfStateLoad = new GltfState();
        var error = gltfDocumentLoad.AppendFromFile(path, gltfStateLoad);
        if (error != Error.Ok) {
            GD.PrintErr($"Couldn't load glTF scene (error code: {error}).");
            return null;
        }
        Node3D gltfParent = new();
        Node3D gltfSceneRootNode = (Node3D)gltfDocumentLoad.GenerateScene(gltfStateLoad);
        TaskCompletionSource tcs = new();
        Callable.From(() => {
            gltfParent.AddChild(gltfSceneRootNode);
            GetTree().Root.AddChild(gltfParent);
            tcs.SetResult();
        }).CallDeferred();
        await tcs.Task;
        return gltfParent;
    }

    private void NormalizeModel(Node3D model)
    {
        Aabb modelBounds = CalculateModelAABB(model);
        
        if (modelBounds.Size == Vector3.Zero)
        {
            GD.PrintErr("Model has zero size - cannot normalize");
            return;
        }
        
        Vector3 center = modelBounds.Position + modelBounds.Size / 2;
        
        // Find the largest dimension
        float maxDimension = Mathf.Max(Mathf.Max(modelBounds.Size.X, modelBounds.Size.Y), modelBounds.Size.Z);
        if (maxDimension <= 0)
        {
            GD.PrintErr("Model has invalid dimensions - cannot normalize");
            return;
        }
        
        // Calculate scale to fit in a unit cube
        float scaleFactor = 1.0f / maxDimension;
        
        model.Position = -center * scaleFactor;  // Center the model at origin
        model.Scale = Vector3.One * scaleFactor; // Scale uniformly to fit in unit cube
    }
    
    private Aabb CalculateModelAABB(Node node)
    {
        Aabb result = new();
        bool isFirst = true;
        
        // Process this node if it's a geometric instance
        if (node is GeometryInstance3D geo)
        {
            Aabb localAabb = geo.GetAabb();
            // Transform local AABB to global space
            Aabb globalAabb = geo.GlobalTransform * localAabb;
            result = globalAabb;
            isFirst = false;
        }
        
        // Process all children
        foreach (Node child in node.GetChildren())
        {
            if (child is Node3D)
            {
                Aabb childAabb = CalculateModelAABB(child);
                if (childAabb.Size != Vector3.Zero)
                {
                    if (isFirst)
                    {
                        result = childAabb;
                        isFirst = false;
                    }
                    else
                    {
                        result = result.Merge(childAabb);
                    }
                }
            }
        }
        
        return result;
    }

    private void ApplyMaterialRecursively(Node root, ShaderMaterial mat)
    {
        var seen = new Dictionary<Material, Material>();
        HashSet<string> targetParams = mat.Shader.GetShaderUniformList().Select(x => x.AsGodotDictionary()["name"].AsString()).ToHashSet();
        OverrideRecursively(root, seen, (source) => {
            var sourceParams = source.GetPropertyList();
            ShaderMaterial target = (ShaderMaterial)mat.Duplicate();
        
            foreach (var paramDict in sourceParams)
            {
                string paramName = paramDict["name"].AsString();
                
                if (targetParams.Contains(paramName))
                {
                    target.SetShaderParameter(paramName, source.Get(paramName));
                }
            }

            return target;
        });
    }

    private void OverrideRecursively(Node node, Dictionary<Material, Material> seen, Func<Material, Material> overrideAction)
    {
        if (node is MeshInstance3D mesh && mesh.Mesh != null)
        {
            int surfaceCount = mesh.GetSurfaceOverrideMaterialCount();
            for (int i = 0; i < surfaceCount; i++)
            {
                var original = mesh.Mesh.SurfaceGetMaterial(i);
                if (original == null) 
                    continue;

                // duplicate once per unique original
                if (!seen.TryGetValue(original, out var copy))
                {
                    copy = overrideAction?.Invoke(original);
                    seen[original] = copy;
                }

                mesh.SetSurfaceOverrideMaterial(i, copy);
            }
        }

        foreach (Node child in node.GetChildren())
        {
            if (child is Node c)
                OverrideRecursively(c, seen, overrideAction);
        }
    }
    
    private async Task<List<long>> RenderViews(List<Vector3> views, string filePrefix, string renderPath)
    {
        if (Viewport == null || Camera == null)
        {
            GD.PrintErr("Cannot render views: Viewport or Camera not assigned!");
            return new();
        }
        
        List<long> taskIds = new();
        
        // Process each view
        for (int i = 0; i < views.Count; i++)
        {
            GD.Print($"Rendering Frame #{i}");
            // Set camera to the current view transform
            Camera.GlobalPosition = views[i];
            Camera.LookAt(Vector3.Zero);
            
            // Force the viewport to render
            //RenderingServer.ViewportSetUpdateMode(Viewport.GetViewportRid(), RenderingServer.ViewportUpdateMode.Once);
            RenderingServer.ForceDraw();
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw); 
            
            var texture = Viewport.GetTexture();
            var snapshot = texture.GetImage();
            taskIds.Add(WorkerThreadPool.AddTask(Callable.From(() => {
                SaveRenderedImage(snapshot, Path.Join(renderPath, $"{filePrefix}_{i}.png"));
            }), true));
        }

        return taskIds;
    }

    private void SaveRenderedImage(Image snapshot, string filePath) {
        GD.Print($"Saving {Time.GetTicksMsec()/1000f}");
        Error err = snapshot.SavePng(filePath);
        
        if (err != Error.Ok)
        {
            GD.PrintErr($"Failed to save image to {filePath}. Error: {err}");
        }
    }
}