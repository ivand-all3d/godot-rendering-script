shader_type spatial;
render_mode unshaded;

uniform vec4 albedo_color : source_color;
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform ivec2 albedo_texture_size;
uniform float point_size : hint_range(0.1, 128.0, 0.1);

void fragment() {
	vec2 base_uv = UV;

	vec4 albedo_tex = texture(albedo_texture, base_uv);
	ALBEDO = albedo_color.rgb * albedo_tex.rgb;
}