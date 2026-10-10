#ifdef GL_ES
precision mediump float;
#endif

uniform sampler2D al_tex;
uniform float bright_amount;
varying vec4 varying_color;
varying vec2 varying_texcoord;


vec4 checker3D(vec3 texc, vec4 color0, vec4 color1)
{
  if ((int(floor(texc.x) + floor(texc.y) + floor(texc.z)) & 1) == 0)
    return color0;
  else
    return color1;
}

void main()
{
   vec4 c = varying_color * texture2D(al_tex, varying_texcoord); // UV

   if (c.a != 0f)
   {
      vec3 color = vec3(20 / 255, 20 / 255, 20 / 255);
      vec2 txc = gl_FragCoord.xy;
      /*if (mod(txc.y, 2) < 1)
      {
         gl_FragColor = vec4(color, 1.0);
      }
      else
      {
         gl_FragColor = vec4(color, 0.0);
      }*/

      if ((int(floor(txc.x) + floor(txc.y)) & 1) == 0)
         gl_FragColor = vec4(color, c.a);
   }

   //if ( nYCnt%2 == 0 ||  nDstYPos < 0 )
}