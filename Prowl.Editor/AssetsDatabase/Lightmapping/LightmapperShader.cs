// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Lightmapping;

/// <summary>
/// The GPU path tracer behind <see cref="GpuLightmapper"/>, three kernels over one BVH of the whole baked scene.
/// <list type="bullet">
/// <item><c>Validate</c> looks around each texel once, pushing a sample that starts inside geometry back out through the
/// face it is behind, and dropping a texel buried in a solid so dilation fills it from its neighbours.</item>
/// <item><c>Trace</c> adds one jittered path to each texel: direct light from the lights that bake theirs, and indirect
/// light from cosine sampled bounces with next event estimation at every hit.</item>
/// <item><c>Probe</c> adds a batch of rays to each probe's spherical harmonics.</item>
/// </list>
/// Everything is in the lightmap's units, irradiance over pi, which is radiance for a uniform surround. Area lights,
/// sized punctual lights and the sun are sampled over their shapes, so their shadows soften the way they do in realtime.
/// </summary>
internal static class LightmapperShader
{
    public const int LightStride = 6;      // float4 per light
    public const int MaterialStride = 4;   // float4 per material
    public const int MapStride = 2;        // uint4 per material
    public const int TexelStride = 4;      // float4 per texel
    public const int SkyWidth = 64, SkyHeight = 32;

    public const float TexelClean = 0f, TexelPushed = 1f, TexelInvalid = 2f;

    public const int LightDirectional = 0, LightPoint = 1, LightSpot = 2, LightRect = 3, LightDisk = 4, LightTube = 5;
    public const int LightBakesDirect = 1, LightCastsShadows = 2;

    public const uint MapSrgb = 1, MapSecondUV = 2;

    public const string Source = """
        #pragma kernel Validate 64
        #pragma kernel Trace 64
        #pragma kernel Probe 64

        // ---------------------------------------------------------------- scene

        // Two per node, its bounds.
        layout(std430) readonly buffer Lightmap_Bounds { vec4 bounds[]; };
        // Per node: a leaf's first triangle and count, or an inner node's left child (the right follows it) and 0.
        layout(std430) readonly buffer Lightmap_Links { uvec2 links[]; };
        // Three per triangle: first corner, then the two edges from it.
        layout(std430) readonly buffer Lightmap_Triangles { vec4 triangles[]; };
        layout(std430) readonly buffer Lightmap_TriangleMaterials { uint triangleMaterials[]; };
        // Three per triangle, the corner normals.
        layout(std430) readonly buffer Lightmap_Normals { vec4 normals[]; };
        // Three per triangle, the corner UVs: first set in xy, second in zw.
        layout(std430) readonly buffer Lightmap_UVs { vec4 uvs[]; };
        // Four per material: base colour + alpha cutoff (negative for opaque), emitted radiance + base alpha, base map
        // scale and offset, emission map scale and offset.
        layout(std430) readonly buffer Lightmap_Materials { vec4 materials[]; };
        // Two per material, its base and emission maps: first texel in the pool, width, height (0 for none), flags.
        layout(std430) readonly buffer Lightmap_Maps { uvec4 maps[]; };
        // RGBA8 texels of every map, bottom row first.
        layout(std430) readonly buffer Lightmap_Pool { uint pool[]; };
        // Six per light: position + type, forward + range (0 unbounded), colour + flags, outer and inner cone cosines,
        // radius (the cosine of the sun's angular radius) and surface over projected area, then two shape axes, each
        // scaled to its half extent.
        layout(std430) readonly buffer Lightmap_Lights { vec4 lights[]; };
        // Radiance from every direction for rays that leave the scene, latitude by longitude, +Y up.
        layout(std430) readonly buffer Lightmap_Sky { vec4 sky[]; };

        #if defined(KERNEL_Validate) || defined(KERNEL_Trace)
        // Four per texel: position + world radius, normal + state, then the world step to the next texel along x and y.
        layout(std430) buffer Lightmap_Texels { vec4 texels[]; };
        #endif
        #ifdef KERNEL_Trace
        // Per texel: direct light summed + sample count, indirect light summed + its luminance squared summed.
        layout(std430) buffer Lightmap_Direct { vec4 direct[]; };
        layout(std430) buffer Lightmap_Indirect { vec4 indirect[]; };
        // Per texel: the directions light arrived from, each weighted by its luminance, summed + those luminances summed.
        layout(std430) buffer Lightmap_Direction { vec4 direction[]; };
        #endif
        #ifdef KERNEL_Probe
        layout(std430) readonly buffer Lightmap_Probes { vec4 probes[]; };
        // Nine per probe, the running sum of each coefficient.
        layout(std430) buffer Lightmap_ProbeSH { vec4 probeSH[]; };
        #endif

        uniform int _LightCount;
        uniform int _Bounces;
        uniform int _Iteration;
        uniform int _Samples;
        uniform int _Offset;
        uniform int _Count;
        uniform int _Cull;
        uniform int _IgnoreAlbedo;
        uniform int _Sky;
        uniform float _Bias;
        uniform float _MaxDistance;
        uniform int _ProbeRays;
        uniform float _ProbeDirectWeight;

        const float PI = 3.14159265359;
        const float INF = 1e30;
        const uint DIRECTIONAL = 0u, POINT = 1u, SPOT = 2u, RECT = 3u, DISK = 4u, TUBE = 5u;
        const uint BAKES_DIRECT = 1u, CASTS_SHADOWS = 2u;
        const uint MAP_SRGB = 1u, MAP_SECOND_UV = 2u;
        const int SKY_WIDTH = 64, SKY_HEIGHT = 32;
        const float CLEAN = 0.0, PUSHED = 1.0, INVALID = 2.0;

        // ---------------------------------------------------------------- random

        uint s_rng;

        uint Pcg(uint v)
        {
            uint state = v * 747796405u + 2891336453u;
            uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
            return (word >> 22u) ^ word;
        }

        float Random()
        {
            s_rng = Pcg(s_rng);
            return float(s_rng >> 8) * (1.0 / 16777216.0);
        }

        // An orthonormal basis around n, Duff et al. 2017
        void Basis(vec3 n, out vec3 t, out vec3 b)
        {
            float s = n.z >= 0.0 ? 1.0 : -1.0;
            float a = -1.0 / (s + n.z);
            float c = n.x * n.y * a;
            t = vec3(1.0 + s * n.x * n.x * a, s * c, -s * n.x);
            b = vec3(c, s + n.y * n.y * a, -n.y);
        }

        vec3 Around(vec3 n, vec3 local)
        {
            vec3 t, b;
            Basis(n, t, b);
            return normalize(t * local.x + b * local.y + n * local.z);
        }

        vec3 CosineDirection(vec3 n)
        {
            float u1 = Random(), u2 = Random();
            float r = sqrt(u1), phi = 2.0 * PI * u2;
            return Around(n, vec3(r * cos(phi), r * sin(phi), sqrt(max(1.0 - u1, 0.0))));
        }

        vec3 SphereDirection()
        {
            float z = 1.0 - 2.0 * Random();
            float r = sqrt(max(0.0, 1.0 - z * z));
            float phi = 2.0 * PI * Random();
            return vec3(r * cos(phi), r * sin(phi), z);
        }

        // ---------------------------------------------------------------- materials

        float SrgbToLinear(float c) { return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4); }

        vec4 MapTexel(uvec4 map, int x, int y)
        {
            int w = int(map.y), h = int(map.z);
            x = ((x % w) + w) % w;
            y = ((y % h) + h) % h;
            vec4 c = unpackUnorm4x8(pool[map.x + uint(y * w + x)]);
            if ((map.w & MAP_SRGB) != 0u) c.rgb = vec3(SrgbToLinear(c.r), SrgbToLinear(c.g), SrgbToLinear(c.b));
            return c;
        }

        // A map at the triangle's point, repeating and filtered between its four nearest texels. White when the
        // material has none.
        vec4 SampleMap(uint material, uint slot, uint tri, vec2 bary)
        {
            uvec4 map = maps[material * 2u + slot];
            if (map.z == 0u) return vec4(1.0);
            float w = 1.0 - bary.x - bary.y;
            vec4 c = uvs[tri * 3u] * w + uvs[tri * 3u + 1u] * bary.x + uvs[tri * 3u + 2u] * bary.y;
            vec2 uv = (map.w & MAP_SECOND_UV) != 0u ? c.zw : c.xy;
            vec4 st = materials[material * 4u + 2u + slot];
            uv = uv * st.xy + st.zw;

            vec2 p = uv * vec2(float(map.y), float(map.z)) - 0.5;
            vec2 f = fract(p);
            ivec2 i = ivec2(floor(p));
            return mix(mix(MapTexel(map, i.x, i.y), MapTexel(map, i.x + 1, i.y), f.x),
                       mix(MapTexel(map, i.x, i.y + 1), MapTexel(map, i.x + 1, i.y + 1), f.x), f.y);
        }

        // Whether a cutout surface is a hole at this point.
        bool CutAway(uint tri, vec2 bary)
        {
            uint m = triangleMaterials[tri];
            float cutoff = materials[m * 4u].w;
            if (cutoff < 0.0) return false;
            return materials[m * 4u + 1u].w * SampleMap(m, 0u, tri, bary).a < cutoff;
        }

        // ---------------------------------------------------------------- tracing

        float Slab(uint node, vec3 ro, vec3 invDir, float tMax)
        {
            vec3 lo = (bounds[node * 2u].xyz - ro) * invDir;
            vec3 hi = (bounds[node * 2u + 1u].xyz - ro) * invDir;
            vec3 tn = min(lo, hi), tf = max(lo, hi);
            float t0 = max(max(tn.x, tn.y), max(tn.z, 0.0));
            float t1 = min(min(tf.x, tf.y), min(tf.z, tMax));
            return t0 <= t1 ? t0 : INF;
        }

        // The closest surface along the ray before tMax, or with anyHit the first one found. Cutout holes let it through.
        bool Intersect(vec3 ro, vec3 rd, float tMax, bool anyHit, out float tHit, out uint triHit, out vec2 bary)
        {
            vec3 invDir = 1.0 / mix(rd, vec3(1e-20), equal(rd, vec3(0.0)));
            uint stack[64];
            int sp = 0;
            uint node = 0u;
            tHit = tMax;
            triHit = 0u;
            bary = vec2(0.0);
            bool hit = false;

            if (Slab(0u, ro, invDir, tMax) >= INF) return false;
            while (true)
            {
                uvec2 link = links[node];
                if (link.y > 0u)
                {
                    for (uint i = link.x; i < link.x + link.y; i++)
                    {
                        vec3 v0 = triangles[i * 3u].xyz;
                        vec3 e1 = triangles[i * 3u + 1u].xyz;
                        vec3 e2 = triangles[i * 3u + 2u].xyz;
                        vec3 p = cross(rd, e2);
                        float det = dot(e1, p);
                        // Prowl's front faces wind clockwise, so culling keeps the negative determinant
                        if (_Cull != 0 ? det > -1e-12 : abs(det) < 1e-12) continue;
                        float invDet = 1.0 / det;
                        vec3 s = ro - v0;
                        float u = dot(s, p) * invDet;
                        if (u < 0.0 || u > 1.0) continue;
                        vec3 q = cross(s, e1);
                        float v = dot(rd, q) * invDet;
                        if (v < 0.0 || u + v > 1.0) continue;
                        float t = dot(e2, q) * invDet;
                        if (t <= 0.0 || t >= tHit) continue;
                        if (CutAway(i, vec2(u, v))) continue;
                        tHit = t;
                        triHit = i;
                        bary = vec2(u, v);
                        hit = true;
                        if (anyHit) return true;
                    }
                }
                else
                {
                    uint near = link.x, far = link.x + 1u;
                    float tNear = Slab(near, ro, invDir, tHit);
                    float tFar = Slab(far, ro, invDir, tHit);
                    if (tFar < tNear) { uint n = near; near = far; far = n; float t = tNear; tNear = tFar; tFar = t; }
                    if (tNear < INF)
                    {
                        if (tFar < INF && sp < 64) stack[sp++] = far;
                        node = near;
                        continue;
                    }
                }
                if (sp == 0) break;
                node = stack[--sp];
            }
            return hit;
        }

        bool Occluded(vec3 ro, vec3 rd, float dist)
        {
            float t; uint tri; vec2 b;
            return Intersect(ro, rd, dist, true, t, tri, b);
        }

        // Off the surface along its normal, by the bias and by enough to clear float precision far from the origin.
        vec3 Offset(vec3 p, vec3 n)
        {
            return p + n * _Bias + sign(n) * abs(p) * 2e-7;
        }

        // The face's own normal, turned to the side its corner normals face.
        vec3 FaceNormal(uint tri)
        {
            vec3 ng = normalize(cross(triangles[tri * 3u + 1u].xyz, triangles[tri * 3u + 2u].xyz));
            vec3 ns = normals[tri * 3u].xyz + normals[tri * 3u + 1u].xyz + normals[tri * 3u + 2u].xyz;
            return dot(ng, ns) < 0.0 ? -ng : ng;
        }

        struct Hit
        {
            vec3 position;
            vec3 normal;     // the shading normal, turned toward the ray
            vec3 albedo;
            vec3 emission;   // emitted radiance, only from the front
        };

        Hit Surface(uint tri, vec2 bary, vec3 ro, vec3 rd, float t)
        {
            Hit h;
            h.position = ro + rd * t;
            float w = 1.0 - bary.x - bary.y;
            h.normal = normalize(normals[tri * 3u].xyz * w + normals[tri * 3u + 1u].xyz * bary.x + normals[tri * 3u + 2u].xyz * bary.y);
            uint m = triangleMaterials[tri];
            bool front = dot(h.normal, rd) < 0.0;
            if (!front) h.normal = -h.normal;
            h.albedo = _IgnoreAlbedo != 0 ? vec3(1.0) : materials[m * 4u].rgb * SampleMap(m, 0u, tri, bary).rgb;
            h.emission = front ? materials[m * 4u + 1u].rgb * SampleMap(m, 1u, tri, bary).rgb : vec3(0.0);
            return h;
        }

        vec3 SkyRadiance(vec3 d)
        {
            if (_Sky == 0) return vec3(0.0);
            float u = atan(d.z, d.x) * (0.5 / PI) + 0.5;
            float v = acos(clamp(d.y, -1.0, 1.0)) / PI;
            int x = min(int(u * float(SKY_WIDTH)), SKY_WIDTH - 1);
            int y = min(int(v * float(SKY_HEIGHT)), SKY_HEIGHT - 1);
            return sky[y * SKY_WIDTH + x].rgb;
        }

        // ---------------------------------------------------------------- lights

        // Graphite's window: inverse square, faded smoothly to nothing at the range
        float Window(float dist2, float range)
        {
            if (range <= 0.0) return 1.0;
            float factor = dist2 / max(range * range, 1e-6);
            float w = clamp(1.0 - factor * factor, 0.0, 1.0);
            return w * w;
        }

        // One sample of a light seen from p: the direction and distance to the sampled point, and the irradiance over
        // pi it gives a surface facing it. False when the light gives nothing there.
        bool SampleLight(uint l, vec3 p, out vec3 toLight, out float dist, out vec3 light)
        {
            vec4 a = lights[l * 6u], b = lights[l * 6u + 1u], c = lights[l * 6u + 2u], d = lights[l * 6u + 3u];
            vec3 axisX = lights[l * 6u + 4u].xyz, axisY = lights[l * 6u + 5u].xyz;
            uint type = uint(a.w);
            vec3 forward = b.xyz;
            light = c.rgb;
            toLight = vec3(0.0, 1.0, 0.0);
            dist = _MaxDistance;

            if (type == DIRECTIONAL)
            {
                // A direction inside the sun's disk
                float cosMax = d.z;
                float cosT = 1.0 - Random() * (1.0 - cosMax);
                float sinT = sqrt(max(0.0, 1.0 - cosT * cosT));
                float phi = 2.0 * PI * Random();
                toLight = Around(-forward, vec3(sinT * cos(phi), sinT * sin(phi), cosT));
                return true;
            }

            vec3 centre = a.xyz;
            vec3 toCentre = centre - p;
            float centreDist2 = dot(toCentre, toCentre);
            float window = Window(centreDist2, b.w);
            if (window <= 0.0) return false;

            if (type == POINT || type == SPOT)
            {
                float cone = 1.0;
                if (type == SPOT)
                {
                    float cosA = dot(forward, -normalize(toCentre));
                    if (cosA <= d.x) return false;
                    cone = smoothstep(d.x, d.y, cosA);
                }
                // A sized source shadows from a point on its disk as seen from p
                vec3 target = centre;
                if (d.z > 0.0 && centreDist2 > 1e-12)
                {
                    float r = d.z * sqrt(Random()), phi = 2.0 * PI * Random();
                    vec3 t, bt;
                    Basis(normalize(toCentre), t, bt);
                    target += (t * cos(phi) + bt * sin(phi)) * r;
                }
                vec3 delta = target - p;
                dist = length(delta);
                if (dist <= 1e-6) return false;
                toLight = delta / dist;
                light *= window * cone / max(centreDist2, 0.01);
                return true;
            }

            // Area lights: a uniform point on the shape and the normal it shines along there
            vec3 q, nl;
            if (type == RECT || type == DISK)
            {
                vec2 s;
                if (type == RECT) s = vec2(Random(), Random()) * 2.0 - 1.0;
                else { float r = sqrt(Random()), phi = 2.0 * PI * Random(); s = vec2(cos(phi), sin(phi)) * r; }
                q = centre + axisX * s.x + axisY * s.y;
                nl = forward;
            }
            else
            {
                // A capsule along forward: its side or its two caps, by area
                float halfLength = length(axisX), radius = d.z;
                float side = 4.0 * PI * radius * halfLength, caps = 4.0 * PI * radius * radius;
                if (Random() * (side + caps) < side)
                {
                    vec3 t, bt;
                    Basis(forward, t, bt);
                    float phi = 2.0 * PI * Random();
                    nl = t * cos(phi) + bt * sin(phi);
                    q = centre + forward * ((Random() * 2.0 - 1.0) * halfLength) + nl * radius;
                }
                else
                {
                    nl = SphereDirection();
                    q = centre + forward * (sign(dot(nl, forward)) * halfLength) + nl * radius;
                }
            }
            vec3 delta = q - p;
            float dist2 = max(dot(delta, delta), 1e-8);
            dist = sqrt(dist2);
            toLight = delta / dist;
            float cosL = dot(nl, -toLight);
            if (cosL <= 0.0) return false;
            // Uniform over the surface: the axis intensity over pi times surface over projected area cancels the area
            light *= d.w * cosL * window / max(dist2, 0.01);
            return true;
        }

        float Luminance(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

        // Light at a surface from every light, shadowed, or only from the lights that bake their direct light. Adds the
        // direction each light arrives from, weighted by its luminance, to arrivals.
        vec3 DirectLight(vec3 p, vec3 n, bool bakedOnly, inout vec4 arrivals)
        {
            vec3 sum = vec3(0.0);
            vec3 ro = Offset(p, n);
            for (int i = 0; i < _LightCount; i++)
            {
                uint l = uint(i);
                uint flags = uint(lights[l * 6u + 2u].w);
                if (bakedOnly && (flags & BAKES_DIRECT) == 0u) continue;
                vec3 toLight; float dist; vec3 light;
                if (!SampleLight(l, p, toLight, dist, light)) continue;
                float ndotl = dot(n, toLight);
                if (ndotl <= 0.0) continue;
                if ((flags & CASTS_SHADOWS) != 0u && Occluded(ro, toLight, dist - 2.0 * _Bias)) continue;
                vec3 c = light * ndotl;
                sum += c;
                float lum = Luminance(c);
                arrivals += vec4(toLight * lum, lum);
            }
            return sum;
        }

        vec3 DirectLight(vec3 p, vec3 n, bool bakedOnly)
        {
            vec4 ignored = vec4(0.0);
            return DirectLight(p, n, bakedOnly, ignored);
        }

        // Light reaching p from cosine sampled paths of up to _Bounces bounces, every hit lit by every light and adding
        // its own emission. Paths past the second bounce end at random, weighted to stay unbiased. firstDirection is
        // the way the path left p, which all of its light arrives along.
        vec3 Indirect(vec3 p, vec3 n, out vec3 firstDirection)
        {
            vec3 radiance = vec3(0.0);
            vec3 throughput = vec3(1.0);
            firstDirection = n;
            for (int bounce = 0; bounce < _Bounces; bounce++)
            {
                vec3 rd = CosineDirection(n);
                if (bounce == 0) firstDirection = rd;
                vec3 ro = Offset(p, n);
                float t; uint tri; vec2 bary;
                if (!Intersect(ro, rd, _MaxDistance, false, t, tri, bary))
                {
                    radiance += throughput * SkyRadiance(rd);
                    break;
                }
                Hit h = Surface(tri, bary, ro, rd, t);
                radiance += throughput * (h.emission + h.albedo * DirectLight(h.position, h.normal, false));
                throughput *= h.albedo;
                if (bounce >= 2)
                {
                    float survive = clamp(max(throughput.r, max(throughput.g, throughput.b)), 0.05, 0.95);
                    if (Random() >= survive) break;
                    throughput /= survive;
                }
                if (throughput.r + throughput.g + throughput.b < 1e-5) break;
                p = h.position;
                n = h.normal;
            }
            return radiance;
        }

        vec3 Indirect(vec3 p, vec3 n)
        {
            vec3 ignored;
            return Indirect(p, n, ignored);
        }

        // ---------------------------------------------------------------- kernels

        #ifdef KERNEL_Validate
        // Rays over the texel's hemisphere, as far as its footprint reaches. Hitting the back of a face means the sample
        // sits inside geometry: a few such hits push it out through the nearest one, many mean it is buried.
        void Validate()
        {
            uint local = gl_GlobalInvocationID.x;
            if (local >= uint(_Count)) return;
            uint texel = uint(_Offset) + local;
            s_rng = Pcg(texel * 2654435761u + 12345u);

            vec4 pr = texels[texel * 4u];
            vec4 nf = texels[texel * 4u + 1u];
            vec3 p = pr.xyz, n = nf.xyz;
            float reach = max(pr.w * 3.0, _Bias * 8.0);
            vec3 ro = Offset(p, n);

            const int Rays = 48;
            int back = 0;
            float nearest = INF;
            vec3 pushTo = p;
            for (int r = 0; r < Rays; r++)
            {
                // Uniform over the hemisphere, so grazing rays toward walls count as much as the ones straight up
                float z = Random(), phi = 2.0 * PI * Random();
                float s = sqrt(max(0.0, 1.0 - z * z));
                vec3 rd = Around(n, vec3(s * cos(phi), s * sin(phi), z));
                float t; uint tri; vec2 bary;
                if (!Intersect(ro, rd, reach, false, t, tri, bary)) continue;
                vec3 ng = FaceNormal(tri);
                if (dot(ng, rd) <= 0.0) continue;
                back++;
                if (t < nearest)
                {
                    nearest = t;
                    pushTo = ro + rd * t + ng * (2.0 * _Bias);
                }
            }

            if (back * 2 > Rays) texels[texel * 4u + 1u].w = INVALID;
            else if (back > 0)
            {
                texels[texel * 4u].xyz = pushTo;
                texels[texel * 4u + 1u].w = PUSHED;
            }
        }
        #endif

        #ifdef KERNEL_Trace
        void Trace()
        {
            uint local = gl_GlobalInvocationID.x;
            if (local >= uint(_Count)) return;
            uint texel = uint(_Offset) + local;
            vec4 nf = texels[texel * 4u + 1u];
            if (nf.w == INVALID) return;
            vec3 centre = texels[texel * 4u].xyz;
            vec3 n = nf.xyz;
            vec3 du = texels[texel * 4u + 2u].xyz, dv = texels[texel * 4u + 3u].xyz;

            // _Samples paths from _Iteration on, each seeded by its own index, so how the passes are split over
            // dispatches never changes the result
            vec4 dSum = vec4(0.0), iSum = vec4(0.0), arrivals = vec4(0.0);
            for (int s = 0; s < _Samples; s++)
            {
                s_rng = Pcg(texel * 9781u + Pcg(uint(_Iteration + s) + 0x9E3779B9u));

                // Anywhere in the texel's footprint, which antialiases shadow edges, unless it had to be pushed clear
                vec3 p = centre;
                if (nf.w == CLEAN)
                    p += du * (Random() - 0.5) + dv * (Random() - 0.5);

                vec4 sampleArrivals = vec4(0.0);
                vec3 d = DirectLight(p, n, true, sampleArrivals);
                vec3 firstDirection;
                vec3 i = Indirect(p, n, firstDirection);
                if (any(isnan(d)) || any(isinf(d))) { d = vec3(0.0); sampleArrivals = vec4(0.0); }
                if (any(isnan(i)) || any(isinf(i))) i = vec3(0.0);
                float l = Luminance(i);
                dSum += vec4(d, 1.0);
                iSum += vec4(i, l * l);
                arrivals += sampleArrivals + vec4(firstDirection * l, l);
            }
            direct[texel] += dSum;
            indirect[texel] += iSum;
            direction[texel] += arrivals;
        }
        #endif

        #ifdef KERNEL_Probe
        void Probe()
        {
            uint probe = gl_GlobalInvocationID.x;
            if (probe >= uint(_Count)) return;
            s_rng = Pcg(probe * 7919u + Pcg(uint(_Iteration) * 31u + 17u));
            vec3 origin = probes[probe].xyz;

            vec3 sh[9];
            for (int k = 0; k < 9; k++) sh[k] = vec3(0.0);

            // Uniform rays over the sphere, each weighted by 4 pi: the CPU divides by the ray count at the end. Lights
            // that bake their direct light then add themselves as one sample each, the rest reach a probe only bounced.
            for (int r = 0; r < _ProbeRays + _LightCount; r++)
            {
                vec3 dir;
                vec3 c;
                if (r < _ProbeRays)
                {
                    dir = SphereDirection();
                    float t; uint tri; vec2 bary;
                    vec3 li;
                    if (!Intersect(origin, dir, _MaxDistance, false, t, tri, bary)) li = SkyRadiance(dir);
                    else
                    {
                        Hit h = Surface(tri, bary, origin, dir, t);
                        li = h.emission + h.albedo * (DirectLight(h.position, h.normal, false) + Indirect(h.position, h.normal));
                    }
                    c = li * (4.0 * PI);
                }
                else
                {
                    // Irradiance over pi times pi is the light's radiance times its solid angle again
                    if (_ProbeDirectWeight <= 0.0) break;
                    uint l = uint(r - _ProbeRays);
                    uint flags = uint(lights[l * 6u + 2u].w);
                    if ((flags & BAKES_DIRECT) == 0u) continue;
                    vec3 light; float dist;
                    if (!SampleLight(l, origin, dir, dist, light)) continue;
                    if ((flags & CASTS_SHADOWS) != 0u && Occluded(origin, dir, dist - 2.0 * _Bias)) continue;
                    c = light * (PI * _ProbeDirectWeight);
                }
                if (any(isnan(c)) || any(isinf(c))) continue;
                sh[0] += c * 0.2820947918;
                sh[1] += c * (0.4886025119 * dir.y);
                sh[2] += c * (0.4886025119 * dir.z);
                sh[3] += c * (0.4886025119 * dir.x);
                sh[4] += c * (1.0925484306 * dir.x * dir.y);
                sh[5] += c * (1.0925484306 * dir.y * dir.z);
                sh[6] += c * (0.3153915652 * (3.0 * dir.z * dir.z - 1.0));
                sh[7] += c * (1.0925484306 * dir.x * dir.z);
                sh[8] += c * (0.5462742153 * (dir.x * dir.x - dir.y * dir.y));
            }

            for (int k = 0; k < 9; k++)
                probeSH[probe * 9u + uint(k)] += vec4(sh[k], 0.0);
        }
        #endif
        """;
}
