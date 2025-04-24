shader_type spatial;
render_mode unshaded;

// Expose your camera’s near/far planes
uniform float z_near : hint_range(0.01, 10.0) = 0.1;
uniform float z_far  : hint_range(10.0, 1000.0) = 100.0;

// Varyings to carry data from vertex → fragment
varying vec3 ws_normal;
varying float view_z;

void vertex() {
    // 1) World‐space normal (using proper normal matrix)
    ws_normal = (MODEL_NORMAL_MATRIX * NORMAL).xyz;

    // 2) Compute view‐space Z
    vec4 world_pos = MODEL_MATRIX * vec4(VERTEX, 1.0);
    vec4 view_pos  = INV_VIEW_MATRIX * world_pos;
    view_z = -view_pos.z;
}

void fragment() {
    // --- RGB: world‐space normal ---
    vec3 n = normalize(ws_normal);
    ALBEDO = n * 0.5 + 0.5;  // remap [-1,1] → [0,1]

    // --- A: linear depth ---
    //float lin_d = clamp((view_z - z_near) / (z_far - z_near), 0.0, 1.0);
    //ALPHA = lin_d;
}
