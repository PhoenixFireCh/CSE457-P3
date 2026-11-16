using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CielaSpike;
using DefaultNamespace;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static DefaultNamespace.MeshObject;

[RequireComponent(typeof(Camera))]
public class RayTracer : MonoBehaviour
{
    // Don't change these! Render the image as 512x512 for
    // the grade checking tool to work
    private const int Width = 512; 
    private const int Height = 512;
    private const int NTasks = 16; // number of parallel tasks to speed up raytracing
    public int MaxRecursionDepth = 3;

    public GameObject imageSavedText;
    public GameObject rawImage;
    public Camera renderCamera;
    public RenderTexture renderTexture;
    
    // You'll probably don't need to use these variables
    private CameraObject _cameraObject; // holds renderCamera data
    private List<MeshObject> _meshObjects; // stores all mesh objects in the scene
    private Color32[] _colors; // stores computed colors
    private Texture2D _tex2d; // texture that holds _colors
    private Ray _debugRay; // debug ray in Editor

    
    // You'll probably need to use these variables
    private BVH _bvh; // an instance of Bounding Volume Hierarchy acceleration structure, used to check for intersection
    private List<PointLightObject> _pointLightObjects; // point lights in the scene
    private Color _ambientColor; // ambient light in the scene
    private static readonly Color ReflectionRayColor = Color.blue;
    private static readonly Color RefractionRayColor = Color.yellow;
    private static readonly Color ShadowRayColor = Color.magenta;

    /// <summary>
    /// Initialize the necessary data and start tracing the scene
    /// (DO NOT MODIFY)
    /// </summary>
    public void Awake()
    {
        imageSavedText.SetActive(false);
        _colors = new Color32[Width * Height]; // holds ray-traced colors
        _tex2d = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        
        _cameraObject = new CameraObject(Width, Height, renderCamera.cameraToWorldMatrix,
            Matrix4x4.Inverse(renderCamera.projectionMatrix), renderCamera.transform.position); // Initialize an instance of RenderCamera
        _meshObjects = CollectMeshes(); // Collect all meshes in the scene
        _pointLightObjects = CollectPointLights(); // Collect all point lights in the scene
        _ambientColor = RenderSettings.ambientLight; // Get the scene's ambient light
        _bvh = new BVH(_meshObjects); // Initialize an instance of accelerated ray-tracing structure
        
        StartCoroutine(TraceScene()); // Trace the scene
    }
    

    /// <summary>
    /// Trace the scene. We do this by tracing rays for each block of rows (TraceRows()) in parallel
    /// (DO NOT MODIFY)
    /// </summary>
    /// <returns></returns>
    private IEnumerator TraceScene()
    {
        List<Task> tasks = new List<Task>();
        
        var px = Width / NTasks;
        for (var i = 0; i < NTasks; i++) tasks.Add(new Task(TraceRows(0, Math.Min(px, Height))));
        // Initialize parallel ray tracing computations for each block of row
        for (var i = 0; i < NTasks; i++)
        {
            var startRow = i * px;
            var endRow = Math.Min((i + 1) * px, Height);
            Task task;
            this.StartCoroutineAsync(TraceRows(startRow, endRow), out task);
            tasks[i] = task;
        }

        for (var i = 0; i < NTasks; i++) yield return StartCoroutine(tasks[i].Wait());
        
        StartCoroutine(SaveTextureToFile()); // Save rendered image when complete
    }

    /// <summary>
    /// Trace rays from startRow to endRow
    /// (DO NOT MODIFY)
    /// </summary>
    /// <param name="startRow">the starting row</param>
    /// <param name="endRow">the ending row</param>
    /// <returns></returns>
    private IEnumerator TraceRows(int startRow, int endRow)
    {
        for (var i = startRow; i < endRow; i++)
        {
            for (var j = 0; j < Width; j++)
            {
                var ray = _cameraObject.ScreenToWorldRay(new Vector2(j, i));
                _colors[i * Width + j] = TraceRay(ray, 0, false, Color.red);
            }

            yield return Ninja.JumpToUnity;
            _tex2d.SetPixels32(_colors);
            _tex2d.Apply();
            rawImage.GetComponent<RawImage>().texture = _tex2d;
            yield return Ninja.JumpBack;
        }
    }

    /// <summary>
    /// Trace a ray from the camera to a point on the screen and return the final color
    /// </summary>
    /// <param name="ray">a ray with origin and direction</param>
    /// <param name="recursionDepth">the current recursive level</param>
    /// <param name="debug">whether to draw the ray in the Editor</param>
    /// <param name="rayColor">the color (type) of the ray</param>
    /// <returns>the final color at a pixel</returns>
    private Color TraceRay(Ray ray, int recursionDepth, bool debug, Color rayColor)
    {
        // It's a good idea to have the recursive base case at the top of the function 
        // (if (recurseDepth >= max) then return Color.black)
        if (recursionDepth >= MaxRecursionDepth) return Color.black;

        Intersection hit;
        bool isHit = _bvh.IntersectBoundingBox(ray, out hit);  // IntersectBoundingBox checks for a potential intersection for a ray
    
        if (debug)    // Draw the rays
        {
            var hitPoint = ray.GetPoint(1000);
            if (isHit)
            {
                hitPoint = hit.point;
                Debug.DrawLine(hit.point, hit.point + (float)0.2 * hit.normal, Color.green);
            }
    
            Debug.DrawLine(ray.origin, hitPoint, rayColor);
        }
        
        if (!isHit) return Color.black; // Returns black when there's no intersection

        // An intersection occured, now get the necessary components
        var mat = hit.material;
        var kd = mat.Kd; // Diffuse component
        var ks = mat.Ks; // Specular component
        var ke = mat.Ke; // Emissive component
        var kt = mat.Kt; // Transparency component (refraction)
    
        var shininess = mat.Shininess;
        var indexOfRefraction = mat.IndexOfRefraction;
    
        var N = hit.normal;
        var V = ray.direction;
        
        Color result = Color.black;

        // (1) It's a good idea to check if the ray is entering or exiting an object...
        bool isExiting = 0 <= Vector3.Dot(V, N);
        if (isExiting) 
        {
            N = -N;
            indexOfRefraction = 1f/indexOfRefraction;
        }
        // (2) Iterate over all point lights in the scene to get total contributions. For each light:
        //    + Calculate point light distance attenuation
        //    + Calculate shadow attenuation
        //    + Calculate the direct contributions (diffuse, specular)
        foreach(PointLightObject l in _pointLightObjects)
        {
            var L = Vector3.Normalize(l.LightPos - hit.point); //Getting vector of light aka the de-facto view
            var H = Vector3.Normalize(new Vector3((L.x + V.x)/2, (L.y + V.y)/2, (L.z + V.z)/2)); //Hopefully yhis works
            //Light distance att calc
            float lAtt = 1f / (1f + Mathf.Pow(Vector3.Distance(hit.point, l.LightPos), 2));
            // Shad att calc
            Ray lightRay = new Ray(hit.point, L);
            Color sAtt = TraceShadow(lightRay, l, debug, ShadowRayColor);
            //Direct cont
            Color attenuation = lAtt * sAtt;
            Color lightColor = l.Color * l.Intensity;
            Color diffuse = kd * Mathf.Max(0f, Vector3.Dot(N, L));
            Color specular = ks * Mathf.Pow(Mathf.Max(0f, Vector3.Dot(N, H)), shininess);
            result += attenuation * lightColor * (diffuse + specular);
        }
        result += ke + kd * _ambientColor;


        // (3) Calculate contributions from reflection and refraction rays (indirect illumination)
        // Make sure to test if the Reflections and Refractions components are non-zero
        //Refraction calculations
        float n = 1f / indexOfRefraction;
        var cosI = -Vector3.Dot(N, V);
        float cosT = Mathf.Sqrt(1f - Mathf.Pow(n, 2) * (1f - Mathf.Pow(cosI, 2)));


        if (Vector3.Magnitude(new Vector3(ks.r, ks.g, ks.b)) > 0f)
        {
            Ray r = new Ray(hit.point, V - 2f * Vector3.Dot(V, N) * N);
            result += ks * TraceRay(r, recursionDepth + 1, debug, ReflectionRayColor);
        }
        if (Vector3.Magnitude(new Vector3(kt.r, kt.g, kt.b)) > 0f && !float.IsNaN(cosT))
        {
            Ray r = new Ray(hit.point, n * V + (n * cosI - cosT) * N);
            result += kt * TraceRay(r, recursionDepth + 1, debug, RefractionRayColor);
        }

        return result;
    }

    /// <summary>
    /// Traces a ray to the lightsource to detect shadows, multiplying by the transparency of each intercected object.
    private Color TraceShadow(Ray ray, PointLightObject l , bool debug, Color rayColor)
    {

        Intersection hit;
        bool isHit = _bvh.IntersectBoundingBox(ray, out hit);
        if (debug)
        {
            var hitPoint = ray.GetPoint(1000);
            if (isHit)
            {
                hitPoint = hit.point;
                Debug.DrawLine(hit.point, hit.point + (float)0.2 * hit.normal, Color.green);
            }
            Debug.DrawLine(ray.origin, hitPoint, rayColor);
        }
        float dist = Vector3.Distance(ray.origin, hit.point);
        if (!isHit || dist > Vector3.Distance(ray.origin, l.LightPos)) return Color.white;
        
        var mat = hit.material;
        var kt = mat.Kt;
        if (Vector3.Magnitude(new Vector3(kt.r, kt.g, kt.b)) > 0f)
        {
            return kt * TraceShadow(new Ray(hit.point, ray.direction), l, debug, rayColor);
        } else
        {
            return Color.black;
        }
    }
    
    /// <summary>
    /// Draw a debug ray when user clicks somewhere in Game View
    /// (DO NOT MODIFY)
    /// </summary>
    public void Update()
    { if (Input.GetMouseButtonDown(0))
        {
            renderCamera.targetTexture = null;
            _debugRay = renderCamera.ScreenPointToRay(Input.mousePosition);
            renderCamera.targetTexture = renderTexture;
        }

        TraceRay(_debugRay,  0, true, Color.red);
    }
    

    /// <summary>
    /// Writes Texture2D to an image file which is used in the grade checking tool (ImageComparison.cs)
    /// (DO NOT MODIFY)
    /// </summary>
    private IEnumerator SaveTextureToFile()
    {
        var bytes = _tex2d.EncodeToPNG();
        var dirPath = Application.dataPath + "/Students/";
        Debug.Log("Rendered image saved to " + dirPath);
        if (!Directory.Exists(dirPath))
            Directory.CreateDirectory(dirPath);
        var mScene = SceneManager.GetActiveScene();
        var sceneName = mScene.name;
        File.WriteAllBytes(dirPath + sceneName + ".png", bytes);

        // Display "Image Saved" text
        imageSavedText.SetActive(true);
        yield return new WaitForSeconds(2);
        imageSavedText.SetActive(false);
    }

    /// <summary>
    /// Find and return all meshes in the scene
    /// (DO NOT MODIFY)
    /// </summary>
    /// <returns>A list of MeshObjects</returns>
    private List<MeshObject> CollectMeshes()
    {
        // Collect all meshes in the scene
        List<MeshObject> meshObjects = new List<MeshObject>();
        var meshRenderers = FindObjectsOfType<MeshRenderer>();

        foreach (var meshRenderer in meshRenderers)
        {
            var go = meshRenderer.gameObject;
            var mat = new Material(meshRenderer.material);
            var type = go.GetComponent<MeshFilter>().mesh.name == "Sphere Instance" ? "Sphere" : "TriMeshes";

            var sphereScale = go.transform.lossyScale;
            var sphereRadius = sphereScale.x / 2.0f; // A sphere so we only need to divide x by 2

            var m = go.GetComponent<MeshFilter>().mesh;
            var mo = new MeshObject(type, go, sphereRadius,
                go.transform.localToWorldMatrix, go.transform.position, mat,
                m.triangles, m.vertices, m.normals);
            meshObjects.Add(mo);
        }
        return meshObjects;
    }
    
    /// <summary>
    /// Find and return all point lights in the scene
    /// (DO NOT MODIFY)
    /// </summary>
    /// <returns>A list of PointLightObject</returns>
    private List<PointLightObject> CollectPointLights()
    {
        List<PointLightObject> lightObjects = new List<PointLightObject>();
        if (FindObjectsOfType(typeof(Light)) is Light[] lights)
        {
            for (var i = 0; i < lights.Length && lights[i].type == LightType.Point; i++)
            {
                var pos = lights[i].transform.position;
                var intensity = lights[i].intensity;
                var color = lights[i].color;
                lightObjects.Add(new PointLightObject(pos, intensity, color));
            }
        }
        return lightObjects;
    }
}