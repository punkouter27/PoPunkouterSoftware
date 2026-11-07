/**
 * gfx-webgl — the WebGL2 (and WebGL1 fallback) renderer behind #app-gpu-backdrop.
 *
 * Loaded by js/gpu-backdrop.js, which owns tier selection, the colour tokens, the glass
 * rect collection and the motion-kit subscription. This file owns pixels only. It exposes
 * one factory, `gfxWebgl.create(canvas)`, returning the renderer contract that
 * gpu-backdrop.js drives (resize / setColors / setQuality / setGlass / frame / dispose).
 *
 * ── WebGL2 pipeline ─────────────────────────────────────────────────────────────────────
 *   1. FIELD     fullscreen → sceneFBO.  Aurora blobs + 3-octave curl-noise flow, dithered.
 *   2. PARTICLES transform feedback advects N points through the same curl field. Drawn as
 *                additive soft sprites into sceneFBO.
 *   3. DOWN/UP   dual-Kawase: sceneFBO → half-res → back up. Two 4-tap passes total; one
 *                more step down to quarter res feeds the bloom.
 *   4. COMPOSITE sceneFBO + blur + glass mask → canvas, with edge refraction, a prism
 *                (per-channel) split on glass edges, bloom, the palette lens and dither.
 *
 * ── Removed 2026-11-07: the "Full" effects ──────────────────────────────────────────────
 * A stable-fluids sim, per-service particle flocks, Gray-Scott "scars" around the uptime
 * card, a metaball health orb on its own canvas and the palette's hyperspace jump. All five
 * were opt-in (/settings → Effects → Full) — the default most visitors saw never drew them —
 * yet they were about half of this file, needed float render targets, and more than doubled
 * the measured dispatch cost (~6.4ms vs ~1.6ms). Every one had to be verified by looking
 * at pixels, which is exactly the cost CLAUDE.md warns a decorative layer carries.
 *
 * ── Event effects (driven by gpu-backdrop.js, never by a timer of their own) ────────────
 *   ATTRACT  up to 4 points the particle field bends toward and orbits (live findings).
 *   BURST    up to 4 radial impulses in the particle field (recovery bloom, sign-in comets),
 *            each paired with an expanding warm glow in the composite. The particles alone
 *            are texture, not signal: at half resolution a pushed sprite is a speck.
 *   SWEEP    a radar beam in the composite whose angle is the scan's progress.
 *   SHOCK    one expanding refraction ring — accent on success, colour-split on failure.
 *   LIGHT    a specular sheen on glass edges that follows the pointer / device tilt.
 * All five are uniforms on passes that already run: no extra pass, no extra target. Their
 * ages come from the renderer's own `clock`, which advances only on frames actually drawn,
 * so a scan that finishes in a background tab plays its shockwave when the visitor comes
 * back instead of having expired unseen against wall time.
 *
 * ── Why dual-Kawase and not `backdrop-filter` ───────────────────────────────────────────
 * The 23 per-card `backdrop-filter` roots this layer replaced were the single largest
 * mobile GPU cost in the app, because each one forces its own backdrop snapshot and blur.
 * Dual-Kawase is 8 texture taps total, at quarter the pixels, for the WHOLE page — cheaper
 * than one native blur root, and it composites in the same pass that already runs. The
 * result is a genuinely frosted backdrop behind translucent surfaces rather than a flat
 * tint pretending to be glass.
 *
 * ── Why transform feedback and not a ping-pong FBO ──────────────────────────────────────
 * GPGPU-on-textures needs EXT_color_buffer_float to be renderable, which a meaningful slice
 * of mobile GL drivers do not expose. Transform feedback is core WebGL2, needs no float
 * render target, and keeps particle state in a plain vertex buffer.
 *
 * ── Banding ─────────────────────────────────────────────────────────────────────────────
 * A full-viewport gradient quantised to 8 bits bands visibly — it was the most obvious
 * defect in the previous shader. Both the field pass and the composite apply an ordered
 * dither of ±0.5/255 from a cheap hash. This costs three ALU ops and removes the artefact
 * completely; without it the curl-noise upgrade below would only have made it more obvious.
 *
 * ── WebGL1 fallback ─────────────────────────────────────────────────────────────────────
 * Field pass only, straight to the canvas: no FBOs, no particles, no glass. Same visual
 * identity, none of the cost. It exists so that a driver without WebGL2 still gets a
 * backdrop rather than the flat CSS grid.
 *
 * Simplex noise is Ashima Arts / Stefan Gustavson's public-domain `snoise` (webgl-noise),
 * inlined rather than imported — it is 20 lines and this file has no build step.
 */
(function () {
    'use strict';

    var MAX_GLASS_RECTS = 24;
    var BASE_PARTICLES = 20000;

    // ── Shared GLSL ─────────────────────────────────────────────────────────────────────

    var SNOISE = `
    vec3 mod289(vec3 x){return x-floor(x*(1.0/289.0))*289.0;}
    vec2 mod289(vec2 x){return x-floor(x*(1.0/289.0))*289.0;}
    vec3 permute(vec3 x){return mod289(((x*34.0)+1.0)*x);}
    float snoise(vec2 v){
      const vec4 C=vec4(0.211324865,0.366025403,-0.577350269,0.024390243);
      vec2 i=floor(v+dot(v,C.yy));
      vec2 x0=v-i+dot(i,C.xx);
      vec2 i1=(x0.x>x0.y)?vec2(1.0,0.0):vec2(0.0,1.0);
      vec4 x12=x0.xyxy+C.xxzz; x12.xy-=i1;
      i=mod289(i);
      vec3 p=permute(permute(i.y+vec3(0.0,i1.y,1.0))+i.x+vec3(0.0,i1.x,1.0));
      vec3 m=max(0.5-vec3(dot(x0,x0),dot(x12.xy,x12.xy),dot(x12.zw,x12.zw)),0.0);
      m=m*m; m=m*m;
      vec3 x=2.0*fract(p*C.www)-1.0;
      vec3 h=abs(x)-0.5;
      vec3 ox=floor(x+0.5);
      vec3 a0=x-ox;
      m*=1.79284291400159-0.85373472095314*(a0*a0+h*h);
      vec3 g;
      g.x=a0.x*x0.x+h.x*x0.y;
      g.yz=a0.yz*x12.xz+h.yz*x12.yw;
      return 130.0*dot(m,g);
    }`;

    // Curl of a scalar potential built from three octaves. Divergence-free by construction,
    // which is what makes the particles swirl instead of piling up in sinks.
    var CURL = `
    float potential(vec2 p, float t){
      return snoise(p * 1.0 + vec2(t * 0.10, 0.0)) * 1.00
           + snoise(p * 2.1 + vec2(0.0, t * 0.14)) * 0.45
           + snoise(p * 4.3 - vec2(t * 0.07, t * 0.05)) * 0.20;
    }
    vec2 curl(vec2 p, float t){
      float e = 0.035;
      float dx = potential(p + vec2(e, 0.0), t) - potential(p - vec2(e, 0.0), t);
      float dy = potential(p + vec2(0.0, e), t) - potential(p - vec2(0.0, e), t);
      return vec2(dy, -dx) / (2.0 * e);
    }`;

    // ±0.5/255 of triangular-ish noise. Enough to break 8-bit gradient banding, far below
    // the visible-grain threshold.
    var DITHER = `
    float hash12(vec2 p){
      vec3 p3 = fract(vec3(p.xyx) * 0.1031);
      p3 += dot(p3, p3.yzx + 33.33);
      return fract((p3.x + p3.y) * p3.z);
    }
    vec3 dither(vec3 c, vec2 fragCoord){
      return c + (hash12(fragCoord) - 0.5) / 255.0;
    }`;

    // The aurora identity of the original layer, preserved deliberately: two drifting round
    // blobs plus a sheared band. The curl-noise term modulates them rather than replacing
    // them, so this reads as the same backdrop with more life in it, not a different app.
    var FIELD_BODY = `
      vec2 uv = gl_FragCoord.xy / u_res;
      vec2 p = (uv - 0.5) * vec2(u_res.x / u_res.y, 1.0);
      float t = u_time * 0.05;

      // curl() returns a true derivative (the finite difference is divided by 2*epsilon),
      // so its magnitude is roughly 7 for this 3-octave potential — NOT the ~1 an amplitude
      // reads like. The visible half-extent of p is 0.72, so a multiplier of 0.085 displaces
      // every sample point clean outside its own gaussian falloff and the entire field
      // evaluates to zero. It fails silently and uniformly: no error, no artefact, just a
      // layer that composites to nothing. Keep this term small enough to WARP the blobs
      // rather than teleport them.
      vec2 flow = curl(p * 1.35, u_time * 0.35) * 0.012;
      vec2 pf = p + flow;

      vec2 o1 = vec2(sin(t * 1.1) * 0.35, cos(t * 0.9) * 0.25);
      vec2 o2 = vec2(cos(t * 0.7) * -0.40, sin(t * 1.3) * 0.30);
      float d1 = length(pf - o1);
      float d2 = length(pf - o2);
      float g1 = exp(-d1 * d1 * 3.5);
      float g2 = exp(-d2 * d2 * 4.0);

      float t3 = u_time * 0.021;
      vec2 o3 = vec2(sin(t3 * 0.8) * 0.55, cos(t3 * 0.5) * 0.18 - 0.08);
      vec2 p3 = pf - o3;
      p3.x += p3.y * 0.4;
      float d3 = length(p3 * vec2(2.8, 1.0));
      float g3 = exp(-d3 * d3 * 2.2) * 0.65;

      // Fine filament structure, only where the blobs already are, so it never turns the
      // backdrop into visible noise on a flat area.
      float fil = snoise(pf * 5.5 + flow * 6.0 + vec2(0.0, u_time * 0.06));
      float mask = g1 + g2 + g3;
      float detail = fil * 0.16 * mask;

      // Audio coupling: output level brightens the field and widens the vignette slightly.
      // Bounded at +35% so a loud refresh motif cannot wash the page out.
      float pulse = 1.0 + u_energy * 0.35;

      // 1.0 - smoothstep(lo, hi, x), NOT smoothstep(hi, lo, x). Passing edge0 > edge1 is
      // explicitly undefined in both GLSL and WGSL, and it does not fail loudly — the
      // shader compiles, the layer reports itself healthy, and the driver is free to return
      // zero for every pixel. The original blob shader was written the reversed way, which
      // is why this whole layer composited to very nearly nothing; it went unnoticed because
      // an opaque background on <body> was painting over the canvas anyway (see modern-ui.css).
      float vig = 1.0 - smoothstep(0.15, 1.15 + u_energy * 0.06, length(p));
      // 0.42 / 0.30, not 1.0 / 0.55. Three overlapping gaussians already sum past 1
      // where they meet, and the additive particle pass lands on top of that — the centre
      // of the viewport clipped to pure white and the whole layer read as fog rather than
      // as an aurora behind the page. Headroom here is what keeps the particles legible.
      vec3 col = (u_c1 * g1 + u_c2 * g2 + u_c3 * g3) * pulse * 0.42 + u_c3 * detail * 0.5;
      float a = (mask + detail) * vig * 0.30;`;

    var VERT2 = `#version 300 es
    in vec2 a_pos;
    void main(){ gl_Position = vec4(a_pos, 0.0, 1.0); }`;

    var FIELD_FS2 = `#version 300 es
    precision highp float;
    uniform vec2 u_res; uniform float u_time; uniform float u_energy;
    uniform vec3 u_c1, u_c2, u_c3;
    out vec4 fragColor;
    ${SNOISE}
    ${CURL}
    ${DITHER}
    void main(){
      ${FIELD_BODY}
      fragColor = vec4(dither(col * vig, gl_FragCoord.xy), a);
    }`;

    // ── Particles (WebGL2 only) ─────────────────────────────────────────────────────────

    var PARTICLE_UPDATE_VS = `#version 300 es
    precision highp float;
    in vec2 a_pos; in vec2 a_vel; in float a_seed;
    out vec2 v_pos; out vec2 v_vel; out float v_seed;
    uniform float u_dt; uniform float u_time; uniform float u_aspect; uniform float u_energy;
    uniform vec4 u_attract[4]; uniform int u_attractCount;   // xy = point, z = weight
    uniform vec4 u_burst[4];                                 // xy = origin, z = age s, w = strength
    ${SNOISE}
    ${CURL}
    void main(){
      vec2 f = curl(a_pos * 1.6, u_time * 0.35);
      // Audio raises the drive term, so the field visibly accelerates on a loud transient.
      float drive = 0.18 + u_energy * 0.35;
      vec2 acc = f * drive;

      // Pull toward each live finding, plus a tangential term 1.2x as strong so particles
      // ORBIT the point rather than collapsing onto it — a clump reads as a rendering bug,
      // a slow vortex reads as "look here". Soft 1/(1+4r^2) falloff: a nudge, not a drain.
      for (int i = 0; i < 4; i++){
        if (i >= u_attractCount) break;
        vec2 d = u_attract[i].xy - a_pos;
        float r = length(d) + 0.05;
        vec2 n = d / r;
        acc += (n + vec2(-n.y, n.x) * 1.2) * u_attract[i].z * 0.9 / (1.0 + r * r * 4.0);
      }

      // Radial impulse that dies in ~0.5s — a bloom, not a detonation. It pushes particles
      // past the "hot" speed in the draw pass, which is what makes them glow.
      for (int i = 0; i < 4; i++){
        vec4 b = u_burst[i];
        if (b.w <= 0.0) continue;
        vec2 d = a_pos - b.xy;
        float r = length(d) + 0.02;
        acc += d / r * b.w * 16.0 * exp(-b.z * 2.0) * exp(-r * r * 3.0);
      }

      vec2 vel = a_vel * 0.94 + acc * u_dt;
      vec2 pos = a_pos + vel * u_dt;

      // Toroidal wrap in aspect-corrected clip space. Respawning instead would make the
      // density visibly pulse; wrapping keeps the field statistically stationary.
      vec2 lim = vec2(u_aspect, 1.0) * 1.05;
      pos = mod(pos + lim, 2.0 * lim) - lim;

      v_pos = pos; v_vel = vel; v_seed = a_seed;
      gl_Position = vec4(0.0, 0.0, 0.0, 1.0);
    }`;

    var PARTICLE_DRAW_VS = `#version 300 es
    precision highp float;
    in vec2 a_pos; in vec2 a_vel; in float a_seed;
    out float v_alpha; out vec3 v_col;
    uniform float u_aspect; uniform float u_size; uniform float u_energy;
    uniform vec3 u_c1, u_c2;
    void main(){
      gl_Position = vec4(a_pos / vec2(u_aspect, 1.0), 0.0, 1.0);
      float speed = clamp(length(a_vel) * 1.6, 0.0, 1.0);
      // "Hot" = well above the curl field's steady ~0.7 units/s, so only a burst reaches it
      // and idle drift draws exactly as before. Hot sprites grow, brighten and warm up.
      float hot = smoothstep(1.1, 2.4, length(a_vel));
      gl_PointSize = u_size * (0.55 + a_seed * 0.9) * (1.0 + u_energy * 0.5) * (1.0 + hot * 1.5);
      v_alpha = (0.025 + speed * 0.085) * (0.4 + a_seed * 0.6) + hot * 0.6;
      v_col = mix(u_c1, u_c2, smoothstep(0.62, 1.0, max(a_seed, hot)));
    }`;

    var PARTICLE_DRAW_FS = `#version 300 es
    precision highp float;
    in float v_alpha; in vec3 v_col;
    out vec4 fragColor;
    void main(){
      // Gaussian-ish sprite. A hard disc reads as confetti; the falloff is what makes a
      // few thousand points read as a luminous field.
      float d = length(gl_PointCoord - 0.5) * 2.0;
      float fall = exp(-d * d * 3.2) * step(d, 1.0);
      fragColor = vec4(v_col * fall * v_alpha, fall * v_alpha);
    }`;

    // ── Dual-Kawase ─────────────────────────────────────────────────────────────────────
    // Four bilinear taps per pass, offsets chosen so the two passes together approximate a
    // much wider Gaussian than eight taps normally buy.

    var KAWASE_DOWN_FS = `#version 300 es
    precision highp float;
    uniform sampler2D u_src; uniform vec2 u_texel; uniform float u_offset;
    out vec4 fragColor;
    void main(){
      vec2 uv = gl_FragCoord.xy * u_texel;
      vec2 o = u_texel * u_offset * 0.5;
      vec4 s = texture(u_src, uv + vec2(-o.x, -o.y));
      s += texture(u_src, uv + vec2( o.x, -o.y));
      s += texture(u_src, uv + vec2(-o.x,  o.y));
      s += texture(u_src, uv + vec2( o.x,  o.y));
      fragColor = s * 0.25;
    }`;

    var KAWASE_UP_FS = `#version 300 es
    precision highp float;
    uniform sampler2D u_src; uniform vec2 u_texel; uniform float u_offset;
    out vec4 fragColor;
    void main(){
      vec2 uv = gl_FragCoord.xy * u_texel;
      vec2 o = u_texel * u_offset;
      vec4 s = texture(u_src, uv + vec2(-o.x, 0.0)) + texture(u_src, uv + vec2(o.x, 0.0));
      s += texture(u_src, uv + vec2(0.0, -o.y)) + texture(u_src, uv + vec2(0.0, o.y));
      s += texture(u_src, uv + o * 0.7) + texture(u_src, uv - o * 0.7);
      fragColor = s / 6.0;
    }`;

    // ── Glass mask ──────────────────────────────────────────────────────────────────────
    // One fullscreen pass over up to MAX_GLASS_RECTS rounded-rect SDFs, rendered ONLY when
    // the rect set changes (scroll, resize, layout) — never per frame. The composite then
    // reads the mask's gradient for refraction, which is why a smooth SDF is stored rather
    // than a binary coverage value.

    var MASK_FS = `#version 300 es
    precision highp float;
    uniform vec2 u_res;
    uniform int u_count;
    uniform vec4 u_rects[${MAX_GLASS_RECTS}];   // xy = centre, zw = half-extent (px)
    uniform float u_radius[${MAX_GLASS_RECTS}];
    out vec4 fragColor;
    float sdRoundRect(vec2 p, vec2 b, float r){
      vec2 q = abs(p) - b + r;
      return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
    }
    void main(){
      vec2 p = gl_FragCoord.xy;
      float d = 1e9;
      for (int i = 0; i < ${MAX_GLASS_RECTS}; i++){
        if (i >= u_count) break;
        vec4 r = u_rects[i];
        d = min(d, sdRoundRect(p - r.xy, r.zw, u_radius[i]));
      }
      // Smooth 6px band across the boundary: inside → 1, outside → 0, and a usable
      // gradient in between for the refraction term.
      fragColor = vec4(1.0 - smoothstep(-3.0, 3.0, d), 0.0, 0.0, 1.0);
    }`;

    var COMPOSITE_FS = `#version 300 es
    precision highp float;
    uniform sampler2D u_scene; uniform sampler2D u_blur; uniform sampler2D u_mask;
    uniform vec2 u_res; uniform vec3 u_tint; uniform float u_energy; uniform float u_glass;
    uniform vec2 u_sweep;                       // x = progress 0..1, y = strength 0..1
    uniform vec4 u_shock;                       // xy = origin px, z = age s, w = strength
    uniform vec3 u_shockColor; uniform float u_glitch; uniform float u_time;
    uniform vec3 u_light;                       // xy = px, z = strength
    uniform vec4 u_puff[4];                     // xy = px, z = age s, w = strength (bursts)
    uniform vec3 u_puffColor;
    uniform sampler2D u_bloom; uniform float u_bloomAmt;
    uniform vec3 u_warp;                        // xy = px, z = lens 0..1
    out vec4 fragColor;
    ${DITHER}
    void main(){
      vec2 uv = gl_FragCoord.xy / u_res;
      vec2 e = 1.5 / u_res;
      float big = max(u_res.x, u_res.y);
      vec2 asp = vec2(u_res.x / u_res.y, 1.0);

      // ── Warp (command palette): a lens that magnifies toward the palette ────────────
      vec2 wc = u_warp.xy / u_res;
      float wr = length((uv - wc) * asp);
      if (u_warp.z > 0.001) uv = wc + (uv - wc) * (1.0 - u_warp.z * 0.22 * exp(-wr * wr * 3.0));

      // ── Shock ring: a refraction band expanding from the button that caused it ──────
      float shockFade = u_shock.w * exp(-u_shock.z * 1.6);
      vec2 sd = gl_FragCoord.xy - u_shock.xy;
      float sr = length(sd);
      float ring = shockFade > 0.001
        ? exp(-pow((sr - u_shock.z * big * 0.9) / (big * 0.035), 2.0)) * shockFade : 0.0;
      uv += (sd / max(sr, 1.0)) * ring * 0.012;

      // Failure: a brief horizontal tear on a few scan bands. Hashed per band per 1/20s,
      // so it flickers like a signal fault rather than sliding like a transition.
      float glitch = u_glitch * shockFade;
      float tear = step(0.93, hash12(vec2(floor(uv.y * 38.0), floor(u_time * 20.0))));
      uv.x += tear * glitch * 0.02;

      float m = texture(u_mask, uv).r * u_glass;
      // Central-difference gradient of the mask. Zero in the flat interior and outside, so
      // the refraction offset only bends light near a glass edge — which is exactly where
      // real glass bends it.
      vec2 g = vec2(
        texture(u_mask, uv + vec2(e.x, 0.0)).r - texture(u_mask, uv - vec2(e.x, 0.0)).r,
        texture(u_mask, uv + vec2(0.0, e.y)).r - texture(u_mask, uv - vec2(0.0, e.y)).r);

      vec2 refr = g * 0.020 * u_glass;
      vec4 sharp = texture(u_scene, uv + refr);
      vec4 soft = texture(u_blur, uv + refr);
      vec4 col = mix(sharp, soft, clamp(m, 0.0, 1.0));

      // Pointer proximity, needed early: it scales the prism split below.
      float spec = 0.0;
      if (u_light.z > 0.001){
        vec2 ld = (gl_FragCoord.xy - u_light.xy) / (big * 0.22);
        spec = exp(-dot(ld, ld)) * u_light.z;
      }
      float rim = clamp(length(g) * 22.0, 0.0, 1.0);

      // ── Prism edge: real glass disperses, so each channel refracts by a different amount
      // along the edge normal. Faint everywhere, strong where the pointer is. Only on the
      // edge band (rim > 0), so the four extra taps are spent on a few % of the pixels.
      if (rim > 0.01){
        vec2 ca = normalize(g) * rim * (0.0015 + spec * 0.006);
        float mm = clamp(m, 0.0, 1.0);
        col.r = mix(texture(u_scene, uv + refr + ca).r, texture(u_blur, uv + refr + ca).r, mm);
        col.b = mix(texture(u_scene, uv + refr - ca).b, texture(u_blur, uv + refr - ca).b, mm);
      }

      // Chromatic split on failure only. Four extra taps, skipped entirely when idle.
      if (glitch > 0.001){
        vec2 ca = vec2(glitch * 0.006, 0.0);
        col.r = mix(texture(u_scene, uv + refr + ca).r, texture(u_blur, uv + refr + ca).r, clamp(m, 0.0, 1.0));
        col.b = mix(texture(u_scene, uv + refr - ca).b, texture(u_blur, uv + refr - ca).b, clamp(m, 0.0, 1.0));
      }
      // Event effects are EMISSIVE and summed here, then folded in premultiplied at the end.
      // The canvas is non-premultiplied (premultipliedAlpha: false), so adding to col.rgb
      // alone is multiplied again by a near-zero field alpha on screen — every effect
      // rendered at roughly its intensity squared, i.e. invisible, while reporting healthy.
      vec3 emit = u_shockColor * ring * 0.55;

      // Burst glow: a soft puff that swells and fades with its particle impulse.
      for (int i = 0; i < 4; i++){
        vec4 b = u_puff[i];
        if (b.w <= 0.0) continue;
        vec2 bd = (gl_FragCoord.xy - b.xy) / (big * (0.035 + b.z * 0.09));
        emit += u_puffColor * b.w * 0.5 * exp(-b.z * 1.6) * exp(-dot(bd, bd));
      }

      // ── Radar sweep: the scan's progress as an angle, clockwise from 12 o'clock ─────
      if (u_sweep.y > 0.001){
        vec2 p = (gl_FragCoord.xy - u_res * 0.5) / (u_res.y * 0.5);
        float ang = fract(0.25 - atan(p.y, p.x) / 6.2831853);
        float behind = fract(u_sweep.x - ang);          // 0 at the beam head, 1 a lap behind
        float beam = exp(-behind * 14.0);
        float swept = step(ang, u_sweep.x) * 0.045;     // the covered arc, faintly lit
        float fall = 1.0 - smoothstep(0.1, 1.5, length(p));
        emit += u_tint * (beam * 0.30 + swept) * fall * u_sweep.y;
      }

      // Rim light along the glass edge, lifted slightly by output level so the frame
      // catches the beat of the refresh motif.
      col.rgb += u_tint * rim * (0.045 + u_energy * 0.05);

      // Specular sheen: strongest on the rim nearest the light, faint across the pane face.
      // The rim's colour is spectral by edge angle — a prism catching the light.
      if (spec > 0.001){
        vec3 spectral = 0.5 + 0.5 * cos(6.2831853 * (atan(g.y, g.x) / 6.2831853 + vec3(0.0, 0.33, 0.67)));
        emit += mix(u_tint, vec3(1.0), 0.6) * (rim * 0.30 + clamp(m, 0.0, 1.0) * 0.035) * spec
              + spectral * rim * spec * 0.22;
      }

      // ── Bloom: the quarter-res Kawase level, thresholded. The blur pyramid already
      // exists for the glass, so this is one extra tiny pass and five taps. ────────────
      vec2 bt = 3.0 / u_res;
      vec4 bl = texture(u_bloom, uv) * 0.4 + (texture(u_bloom, uv + bt) + texture(u_bloom, uv - bt)
              + texture(u_bloom, uv + vec2(bt.x, -bt.y)) + texture(u_bloom, uv - vec2(bt.x, -bt.y))) * 0.15;
      vec3 bp = bl.rgb * bl.a;
      emit += bp * smoothstep(0.02, 0.16, dot(bp, vec3(0.299, 0.587, 0.114))) * u_bloomAmt * (1.0 + u_energy * 1.5);

      float ea = clamp(col.a + max(emit.r, max(emit.g, emit.b)), 0.0, 1.0);
      col = vec4((col.rgb * col.a + emit) / max(ea, 1e-4), ea);

      fragColor = vec4(dither(col.rgb, gl_FragCoord.xy), col.a);
    }`;

    // ── WebGL1 fallback shaders ─────────────────────────────────────────────────────────

    var VERT1 = `attribute vec2 a_pos; void main(){ gl_Position = vec4(a_pos, 0.0, 1.0); }`;

    var FIELD_FS1 = `precision highp float;
    uniform vec2 u_res; uniform float u_time; uniform float u_energy;
    uniform vec3 u_c1, u_c2, u_c3;
    ${SNOISE}
    ${CURL}
    ${DITHER}
    void main(){
      ${FIELD_BODY}
      gl_FragColor = vec4(dither(col * vig, gl_FragCoord.xy), a);
    }`;

    // ── GL plumbing ─────────────────────────────────────────────────────────────────────

    function compile(gl, type, src) {
        var s = gl.createShader(type);
        gl.shaderSource(s, src);
        gl.compileShader(s);
        if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
            console.warn('gfx-webgl: shader compile failed\n' + gl.getShaderInfoLog(s));
            gl.deleteShader(s);
            return null;
        }
        return s;
    }

    function link(gl, vsSrc, fsSrc, feedback) {
        var vs = compile(gl, gl.VERTEX_SHADER, vsSrc);
        var fs = compile(gl, gl.FRAGMENT_SHADER, fsSrc);
        if (!vs || !fs) return null;
        var p = gl.createProgram();
        gl.attachShader(p, vs);
        gl.attachShader(p, fs);
        if (feedback) gl.transformFeedbackVaryings(p, feedback, gl.SEPARATE_ATTRIBS);
        gl.linkProgram(p);
        gl.deleteShader(vs);
        gl.deleteShader(fs);
        if (!gl.getProgramParameter(p, gl.LINK_STATUS)) {
            console.warn('gfx-webgl: link failed\n' + gl.getProgramInfoLog(p));
            return null;
        }
        return p;
    }

    /** Cache every active uniform location once — getUniformLocation is not free per frame. */
    function uniforms(gl, prog) {
        var out = {};
        var n = gl.getProgramParameter(prog, gl.ACTIVE_UNIFORMS);
        for (var i = 0; i < n; i++) {
            var name = gl.getActiveUniform(prog, i).name.replace(/\[0\]$/, '');
            out[name] = gl.getUniformLocation(prog, name);
        }
        return out;
    }

    function makeTarget(gl, w, h) {
        var tex = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        var fbo = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        return { tex: tex, fbo: fbo, w: w, h: h };
    }

    function resizeTarget(gl, t, w, h) {
        if (t.w === w && t.h === h) return;
        t.w = w; t.h = h;
        gl.bindTexture(gl.TEXTURE_2D, t.tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    }

    // ── Renderer ────────────────────────────────────────────────────────────────────────

    function create(canvas) {
        var attrs = {
            alpha: true, antialias: false, depth: false, stencil: false,
            premultipliedAlpha: false, powerPreference: 'low-power',
            failIfMajorPerformanceCaveat: true
        };

        var gl = canvas.getContext('webgl2', attrs);
        var isGL2 = !!gl;
        if (!gl) gl = canvas.getContext('webgl', attrs);
        if (!gl) return null;

        // Wide gamut where the screen has it. The colours handed to setColors() must then be
        // P3 coordinates — gpu-backdrop.js converts, keyed off the `p3` flag returned below.
        var p3 = 'drawingBufferColorSpace' in gl && window.matchMedia('(color-gamut: p3)').matches;
        if (p3) gl.drawingBufferColorSpace = 'display-p3';

        var colors = [[0.357, 0.486, 0.839], [0.945, 0.714, 0.388], [0.576, 0.706, 0.961]];
        var quality = { scale: 0.5, particles: 0.55, effects: false };
        var W = 1, H = 1;
        var energy = 0;
        var warp = [0.5, 0.5, 0];                     // uv (y down), lens 0..1 — the palette lens

        // Event-effect state. `clock` advances only on drawn frames — see the header.
        var clock = 0;
        var attract = new Float32Array(16), attractCount = 0;
        var bursts = new Float32Array(16), burstAt = [-1e3, -1e3, -1e3, -1e3], burstNext = 0;
        var burstUniform = new Float32Array(16);
        var burstUv = new Float32Array(8), puffUniform = new Float32Array(16);
        var sweep = [0, 0];
        var shockAt = -1e3, shock = [0, 0, 0], shockColor = [0, 0, 0], glitch = 0;
        var light = [0, 0, 0];

        // Fullscreen triangle. One draw, no index buffer, no wasted diagonal fragments.
        var quad = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, quad);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);

        var fieldProg = link(gl, isGL2 ? VERT2 : VERT1, isGL2 ? FIELD_FS2 : FIELD_FS1);
        if (!fieldProg) return null;
        var fieldU = uniforms(gl, fieldProg);
        var fieldPos = gl.getAttribLocation(fieldProg, 'a_pos');

        var g2 = null;
        if (isGL2) g2 = buildGl2(gl, quad);

        function bindQuad(prog, attrLoc) {
            gl.bindBuffer(gl.ARRAY_BUFFER, quad);
            gl.enableVertexAttribArray(attrLoc);
            gl.vertexAttribPointer(attrLoc, 2, gl.FLOAT, false, 0, 0);
        }

        function drawFullscreen() { gl.drawArrays(gl.TRIANGLES, 0, 3); }

        // ── WebGL2-only sub-pipeline ────────────────────────────────────────────────────
        function buildGl2(gl, quad) {
            var update = link(gl, PARTICLE_UPDATE_VS,
                '#version 300 es\nprecision mediump float;\nout vec4 c;\nvoid main(){ c = vec4(0.0); }',
                ['v_pos', 'v_vel', 'v_seed']);
            var draw = link(gl, PARTICLE_DRAW_VS, PARTICLE_DRAW_FS);
            var down = link(gl, VERT2, KAWASE_DOWN_FS);
            var up = link(gl, VERT2, KAWASE_UP_FS);
            var mask = link(gl, VERT2, MASK_FS);
            var comp = link(gl, VERT2, COMPOSITE_FS);
            if (!update || !draw || !down || !up || !mask || !comp) {
                console.warn('gfx-webgl: WebGL2 pipeline unavailable, falling back to field-only');
                return null;
            }

            var r = {
                update: update, updateU: uniforms(gl, update),
                draw: draw, drawU: uniforms(gl, draw),
                down: down, downU: uniforms(gl, down),
                up: up, upU: uniforms(gl, up),
                mask: mask, maskU: uniforms(gl, mask),
                comp: comp, compU: uniforms(gl, comp),
                scene: null, blurA: null, blurB: null, bloom: null, maskTarget: null,
                bufs: null, tf: null, count: 0, capacity: 0,
                maskDirty: true, glassCount: 0,
                rects: new Float32Array(MAX_GLASS_RECTS * 4),
                radii: new Float32Array(MAX_GLASS_RECTS)
            };
            return r;
        }

        /** (Re)allocate the particle state buffers for the current quality tier. */
        function allocParticles(count) {
            if (!g2 || count === g2.capacity) return;

            if (g2.bufs) g2.bufs.forEach(function (b) { gl.deleteBuffer(b.pos); gl.deleteBuffer(b.vel); gl.deleteBuffer(b.seed); });
            if (g2.tf) gl.deleteTransformFeedback(g2.tf);

            var pos = new Float32Array(count * 2);
            var vel = new Float32Array(count * 2);
            var seed = new Float32Array(count);
            var aspect = W / Math.max(1, H);
            for (var i = 0; i < count; i++) {
                pos[i * 2] = (Math.random() * 2 - 1) * aspect;
                pos[i * 2 + 1] = Math.random() * 2 - 1;
                seed[i] = Math.random();
            }

            function pair() {
                var b = { pos: gl.createBuffer(), vel: gl.createBuffer(), seed: gl.createBuffer() };
                gl.bindBuffer(gl.ARRAY_BUFFER, b.pos); gl.bufferData(gl.ARRAY_BUFFER, pos, gl.DYNAMIC_COPY);
                gl.bindBuffer(gl.ARRAY_BUFFER, b.vel); gl.bufferData(gl.ARRAY_BUFFER, vel, gl.DYNAMIC_COPY);
                gl.bindBuffer(gl.ARRAY_BUFFER, b.seed); gl.bufferData(gl.ARRAY_BUFFER, seed, gl.DYNAMIC_COPY);
                return b;
            }
            g2.bufs = [pair(), pair()];
            g2.tf = gl.createTransformFeedback();
            g2.capacity = count;
            g2.count = count;
        }

        function bindParticleAttribs(prog, src) {
            var l = {
                pos: gl.getAttribLocation(prog, 'a_pos'),
                vel: gl.getAttribLocation(prog, 'a_vel'),
                seed: gl.getAttribLocation(prog, 'a_seed')
            };
            gl.bindBuffer(gl.ARRAY_BUFFER, src.pos);
            gl.enableVertexAttribArray(l.pos); gl.vertexAttribPointer(l.pos, 2, gl.FLOAT, false, 0, 0);
            gl.bindBuffer(gl.ARRAY_BUFFER, src.vel);
            gl.enableVertexAttribArray(l.vel); gl.vertexAttribPointer(l.vel, 2, gl.FLOAT, false, 0, 0);
            gl.bindBuffer(gl.ARRAY_BUFFER, src.seed);
            gl.enableVertexAttribArray(l.seed); gl.vertexAttribPointer(l.seed, 1, gl.FLOAT, false, 0, 0);
        }

        function renderMask() {
            if (!g2 || !g2.maskDirty) return;
            g2.maskDirty = false;
            gl.bindFramebuffer(gl.FRAMEBUFFER, g2.maskTarget.fbo);
            gl.viewport(0, 0, g2.maskTarget.w, g2.maskTarget.h);
            gl.disable(gl.BLEND);
            gl.useProgram(g2.mask);
            bindQuad(g2.mask, gl.getAttribLocation(g2.mask, 'a_pos'));
            gl.uniform2f(g2.maskU.u_res, g2.maskTarget.w, g2.maskTarget.h);
            gl.uniform1i(g2.maskU.u_count, g2.glassCount);
            if (g2.glassCount > 0) {
                gl.uniform4fv(g2.maskU.u_rects, g2.rects);
                gl.uniform1fv(g2.maskU.u_radius, g2.radii);
            }
            gl.clearColor(0, 0, 0, 1);
            gl.clear(gl.COLOR_BUFFER_BIT);
            drawFullscreen();
        }

        function frame(dt, elapsed, level) {
            energy = level || 0;
            clock += dt;
            var aspect = W / Math.max(1, H);

            // ── Field ───────────────────────────────────────────────────────────────────
            var target = g2 ? g2.scene : null;
            gl.bindFramebuffer(gl.FRAMEBUFFER, target ? target.fbo : null);
            gl.viewport(0, 0, W, H);
            gl.disable(gl.BLEND);
            gl.useProgram(fieldProg);
            bindQuad(fieldProg, fieldPos);
            gl.uniform2f(fieldU.u_res, W, H);
            gl.uniform1f(fieldU.u_time, elapsed);
            gl.uniform1f(fieldU.u_energy, energy);
            gl.uniform3fv(fieldU.u_c1, colors[0]);
            gl.uniform3fv(fieldU.u_c2, colors[1]);
            gl.uniform3fv(fieldU.u_c3, colors[2]);
            drawFullscreen();

            if (!g2) return;

            // ── Particle advection (transform feedback) ──────────────────────────────────
            if (g2.count > 0 && dt > 0) {
                gl.useProgram(g2.update);
                bindParticleAttribs(g2.update, g2.bufs[0]);
                gl.uniform1f(g2.updateU.u_dt, dt);
                gl.uniform1f(g2.updateU.u_time, elapsed);
                gl.uniform1f(g2.updateU.u_aspect, aspect);
                gl.uniform1f(g2.updateU.u_energy, energy);
                gl.uniform1i(g2.updateU.u_attractCount, attractCount);
                if (attractCount > 0) gl.uniform4fv(g2.updateU.u_attract, attract);
                for (var b = 0; b < 4; b++) {
                    var age = clock - burstAt[b];
                    burstUniform[b * 4] = bursts[b * 4];
                    burstUniform[b * 4 + 1] = bursts[b * 4 + 1];
                    burstUniform[b * 4 + 2] = age;
                    burstUniform[b * 4 + 3] = age < 2 ? bursts[b * 4 + 3] : 0;
                }
                gl.uniform4fv(g2.updateU.u_burst, burstUniform);

                gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, g2.tf);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 0, g2.bufs[1].pos);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 1, g2.bufs[1].vel);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 2, g2.bufs[1].seed);
                gl.enable(gl.RASTERIZER_DISCARD);
                gl.beginTransformFeedback(gl.POINTS);
                gl.drawArrays(gl.POINTS, 0, g2.count);
                gl.endTransformFeedback();
                gl.disable(gl.RASTERIZER_DISCARD);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 0, null);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 1, null);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 2, null);
                gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, null);

                var t = g2.bufs[0]; g2.bufs[0] = g2.bufs[1]; g2.bufs[1] = t;

                // ── Particle draw (additive into the scene) ──────────────────────────────
                gl.useProgram(g2.draw);
                bindParticleAttribs(g2.draw, g2.bufs[0]);
                gl.uniform1f(g2.drawU.u_aspect, aspect);
                gl.uniform1f(g2.drawU.u_size, Math.max(1.5, 2.6 * quality.scale * 2.0));
                gl.uniform1f(g2.drawU.u_energy, energy);
                gl.uniform3fv(g2.drawU.u_c1, colors[0]);
                gl.uniform3fv(g2.drawU.u_c2, colors[1]);
                gl.enable(gl.BLEND);
                // Alpha channel ONE,ONE: with plain SRC_ALPHA,ONE the scene's alpha grew by a²,
                // and the non-premultiplied composite then scaled each sprite by that — a
                // particle over an empty patch of field drew at ~1% of its colour.
                gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE, gl.ONE, gl.ONE);
                gl.drawArrays(gl.POINTS, 0, g2.count);
                gl.disable(gl.BLEND);
            }

            renderMask();

            // ── Dual-Kawase ─────────────────────────────────────────────────────────────
            gl.bindFramebuffer(gl.FRAMEBUFFER, g2.blurA.fbo);
            gl.viewport(0, 0, g2.blurA.w, g2.blurA.h);
            gl.useProgram(g2.down);
            bindQuad(g2.down, gl.getAttribLocation(g2.down, 'a_pos'));
            gl.activeTexture(gl.TEXTURE0);
            gl.bindTexture(gl.TEXTURE_2D, g2.scene.tex);
            gl.uniform1i(g2.downU.u_src, 0);
            gl.uniform2f(g2.downU.u_texel, 1 / g2.blurA.w, 1 / g2.blurA.h);
            gl.uniform1f(g2.downU.u_offset, 1.25);
            drawFullscreen();

            gl.bindFramebuffer(gl.FRAMEBUFFER, g2.blurB.fbo);
            gl.viewport(0, 0, g2.blurB.w, g2.blurB.h);
            gl.useProgram(g2.up);
            bindQuad(g2.up, gl.getAttribLocation(g2.up, 'a_pos'));
            gl.bindTexture(gl.TEXTURE_2D, g2.blurA.tex);
            gl.uniform1i(g2.upU.u_src, 0);
            gl.uniform2f(g2.upU.u_texel, 1 / g2.blurB.w, 1 / g2.blurB.h);
            gl.uniform1f(g2.upU.u_offset, 2.0);
            drawFullscreen();

            // One more Kawase step down, quarter res, for the bloom's wide glow.
            gl.bindFramebuffer(gl.FRAMEBUFFER, g2.bloom.fbo);
            gl.viewport(0, 0, g2.bloom.w, g2.bloom.h);
            gl.useProgram(g2.down);
            bindQuad(g2.down, gl.getAttribLocation(g2.down, 'a_pos'));
            gl.bindTexture(gl.TEXTURE_2D, g2.blurA.tex);
            gl.uniform1i(g2.downU.u_src, 0);
            gl.uniform2f(g2.downU.u_texel, 1 / g2.bloom.w, 1 / g2.bloom.h);
            gl.uniform1f(g2.downU.u_offset, 2.0);
            drawFullscreen();

            // ── Composite to canvas ─────────────────────────────────────────────────────
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.viewport(0, 0, W, H);
            gl.useProgram(g2.comp);
            bindQuad(g2.comp, gl.getAttribLocation(g2.comp, 'a_pos'));
            gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, g2.scene.tex);
            gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, g2.blurB.tex);
            gl.activeTexture(gl.TEXTURE2); gl.bindTexture(gl.TEXTURE_2D, g2.maskTarget.tex);
            gl.uniform1i(g2.compU.u_scene, 0);
            gl.uniform1i(g2.compU.u_blur, 1);
            gl.uniform1i(g2.compU.u_mask, 2);
            gl.uniform2f(g2.compU.u_res, W, H);
            gl.uniform1f(g2.compU.u_energy, energy);
            gl.uniform1f(g2.compU.u_glass, quality.effects ? 1.0 : 0.55);
            gl.uniform3fv(g2.compU.u_tint, colors[2]);
            var shockAge = clock - shockAt;
            gl.uniform2f(g2.compU.u_sweep, sweep[0], sweep[1]);
            gl.uniform4f(g2.compU.u_shock, shock[0] * W, (1 - shock[1]) * H, shockAge, shockAge < 3 ? shock[2] : 0);
            gl.uniform3fv(g2.compU.u_shockColor, shockColor);
            gl.uniform1f(g2.compU.u_glitch, glitch);
            gl.uniform1f(g2.compU.u_time, clock);
            gl.uniform3f(g2.compU.u_light, light[0] * W, (1 - light[1]) * H, light[2]);
            for (var pb = 0; pb < 4; pb++) {
                var pAge = clock - burstAt[pb];
                puffUniform[pb * 4] = burstUv[pb * 2] * W;
                puffUniform[pb * 4 + 1] = (1 - burstUv[pb * 2 + 1]) * H;
                puffUniform[pb * 4 + 2] = pAge;
                puffUniform[pb * 4 + 3] = pAge < 2 ? bursts[pb * 4 + 3] : 0;
            }
            gl.uniform4fv(g2.compU.u_puff, puffUniform);
            gl.uniform3fv(g2.compU.u_puffColor, colors[1]);
            gl.activeTexture(gl.TEXTURE3); gl.bindTexture(gl.TEXTURE_2D, g2.bloom.tex);
            gl.uniform1i(g2.compU.u_bloom, 3);
            gl.uniform1f(g2.compU.u_bloomAmt, quality.effects ? 0.9 : 0.6);
            gl.uniform3f(g2.compU.u_warp, warp[0] * W, (1 - warp[1]) * H, warp[2]);
            drawFullscreen();
        }

        /** Viewport uv (0..1, y DOWN, as the DOM measures it) → particle space. */
        function toParticle(u, v) {
            return [(u * 2 - 1) * (W / Math.max(1, H)), 1 - v * 2];
        }

        function resize(w, h) {
            W = Math.max(1, w); H = Math.max(1, h);
            canvas.width = W; canvas.height = H;
            gl.viewport(0, 0, W, H);
            if (!g2) return;
            var half = { w: Math.max(1, W >> 1), h: Math.max(1, H >> 1) };
            var quarter = { w: Math.max(1, W >> 2), h: Math.max(1, H >> 2) };
            if (!g2.scene) {
                g2.scene = makeTarget(gl, W, H);
                g2.blurA = makeTarget(gl, half.w, half.h);
                g2.blurB = makeTarget(gl, W, H);
                g2.bloom = makeTarget(gl, quarter.w, quarter.h);
                g2.maskTarget = makeTarget(gl, half.w, half.h);
            } else {
                resizeTarget(gl, g2.scene, W, H);
                resizeTarget(gl, g2.blurA, half.w, half.h);
                resizeTarget(gl, g2.blurB, W, H);
                resizeTarget(gl, g2.bloom, quarter.w, quarter.h);
                resizeTarget(gl, g2.maskTarget, half.w, half.h);
            }
            g2.maskDirty = true;
        }

        return {
            tier: isGL2 ? (g2 ? 'webgl2' : 'webgl2-field') : 'webgl1',
            gl: gl,
            p3: p3,

            resize: resize,

            setColors: function (c) { colors = c; },

            setQuality: function (q) {
                quality = q;
                if (g2) allocParticles(Math.round(BASE_PARTICLES * q.particles));
            },

            /**
             * rects: Float32Array of [centreX, centreY, halfW, halfH, radius] in CSS pixels,
             * already clipped to the viewport by the caller. Uploaded lazily — the mask pass
             * only re-runs when this changes, not every frame.
             */
            setGlass: function (list, count, vw, vh) {
                if (!g2) return;
                var n = Math.min(count, MAX_GLASS_RECTS);
                var sx = g2.maskTarget ? g2.maskTarget.w / vw : 1;
                var sy = g2.maskTarget ? g2.maskTarget.h / vh : 1;
                for (var i = 0; i < n; i++) {
                    // Y flips: DOM origin is top-left, gl_FragCoord is bottom-left.
                    g2.rects[i * 4] = list[i * 5] * sx;
                    g2.rects[i * 4 + 1] = (vh - list[i * 5 + 1]) * sy;
                    g2.rects[i * 4 + 2] = list[i * 5 + 2] * sx;
                    g2.rects[i * 4 + 3] = list[i * 5 + 3] * sy;
                    g2.radii[i] = list[i * 5 + 4] * sx;
                }
                g2.glassCount = n;
                g2.maskDirty = true;
            },

            frame: frame,

            // ── Event effects. Every coordinate is viewport uv, y down. No-ops on WebGL1:
            // the uniforms they feed only exist in the WebGL2 passes. ──────────────────────

            /** list: [u, v, weight] × n, n ≤ 4. */
            setAttractors: function (list, n) {
                attractCount = Math.min(4, n | 0);
                for (var i = 0; i < attractCount; i++) {
                    var p = toParticle(list[i * 3], list[i * 3 + 1]);
                    attract[i * 4] = p[0]; attract[i * 4 + 1] = p[1]; attract[i * 4 + 2] = list[i * 3 + 2];
                }
            },

            /** Radial particle impulse. Four slots, oldest overwritten. */
            burst: function (u, v, strength) {
                var p = toParticle(u, v), i = burstNext;
                burstNext = (burstNext + 1) % 4;
                bursts[i * 4] = p[0]; bursts[i * 4 + 1] = p[1]; bursts[i * 4 + 3] = strength;
                burstUv[i * 2] = u; burstUv[i * 2 + 1] = v;
                burstAt[i] = clock;
            },

            /** progress 0..1, strength 0..1 (0 hides the beam). */
            setSweep: function (progress, strength) { sweep[0] = progress; sweep[1] = strength; },

            /** One ring at a time; a new one replaces the last. glitch 0..1 adds the tear/split. */
            shock: function (u, v, rgb, glitchAmount) {
                shock[0] = u; shock[1] = v; shock[2] = 1;
                shockColor = rgb; glitch = glitchAmount || 0;
                shockAt = clock;
            },

            setLight: function (u, v, strength) { light[0] = u; light[1] = v; light[2] = strength; },

            /** Lens toward (u, v), 0..1: magnifies the backdrop toward the open command palette. */
            setWarp: function (u, v, lens) { warp[0] = u; warp[1] = v; warp[2] = lens; },

            dispose: function () {
                var lose = gl.getExtension('WEBGL_lose_context');
                if (lose) lose.loseContext();
            }
        };
    }

    window.gfxWebgl = { create: create };
})();
