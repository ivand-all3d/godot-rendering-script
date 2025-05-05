shader_type spatial;
render_mode unshaded;

uniform float roughness;
uniform sampler2D roughness_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform int roughness_texture_channel;
uniform float metallic;
uniform sampler2D metallic_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform int metallic_texture_channel;

void fragment() {
	vec2 base_uv = UV;

	ALBEDO = vec3(
		0,
		roughness * texture(roughness_texture, base_uv)[roughness_texture_channel],
		metallic * texture(metallic_texture, base_uv)[metallic_texture_channel]
	);
}