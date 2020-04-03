using UnityEngine;

namespace Valcour.UnityUtilities
{
    public class EditorStyleMovement : MonoBehaviour
    {
        [SerializeField]
        private float slowMoveSpeed = 3;
        [SerializeField]
        private float fastMoveSpeed = 10;
        [SerializeField]
        private float mouseSensitivity = 3;
        [SerializeField]
        private Vector3 cameraOffset = new Vector3(0f, 1.778f, -3f);
        [SerializeField]
        private bool actuallyBeLikeEditorStyleMovement = true;

#if UNITY_EDITOR

        private void Start()
        {
            // Lift camera off floor if playing editor 2D (shouldn't affect editor VR)
            if (Camera.main != null)
            {
                var cameraTransform = Camera.main.transform;

                cameraTransform.position = cameraTransform.position + cameraOffset;
            }
        }

        private void Update()
        {
            // Only apply movement behavior if right click is down.
            if (Input.GetMouseButton(1))
            {
                // Lock the mouse, so we can start tracking its deltas. Only look at deltas after the mouse has been locked, to avoid big jumps.
                if (Cursor.lockState != CursorLockMode.Locked)
                {
                    Cursor.lockState = CursorLockMode.Locked;
                }
                else
                {
                    ApplyKeyboardMovement();
                    ApplyMouseMovement();
                }
            }
            else
            {
                if (Cursor.lockState != CursorLockMode.None)
                {
                    Cursor.lockState = CursorLockMode.None;
                }
            }
        }
#endif

        private void ApplyMouseMovement()
        {
            float xAngle = Input.GetAxis("Mouse X") * mouseSensitivity;
            this.gameObject.transform.RotateAround(this.gameObject.transform.position, Vector3.up, xAngle);

            float yAngle = Input.GetAxis("Mouse Y") * mouseSensitivity;
            Vector3 yAxis = this.gameObject.transform.TransformDirection(-Vector3.right);
            this.gameObject.transform.RotateAround(this.gameObject.transform.position, yAxis, yAngle);
        }

        private void ApplyKeyboardMovement()
        {
            // Move fast when shift is held down.
            float speed = slowMoveSpeed;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                speed = fastMoveSpeed;
            }

            float delta = speed * Time.deltaTime;

            // Look for keyboard based movement.
            Vector3 delta3 = new Vector3(0, 0, 0);
            if (Input.GetKey(KeyCode.D))
            {
                delta3.x += delta;
            }
            if (Input.GetKey(KeyCode.A))
            {
                delta3.x -= delta;
            }
            if (Input.GetKey(KeyCode.W))
            {
                delta3.z += delta;
            }
            if (Input.GetKey(KeyCode.S))
            {
                delta3.z -= delta;
            }

            float deltaRot = 45 * Time.deltaTime;
            Vector3 angles = new Vector3(0, 0, 0);

            if (Input.GetKey(KeyCode.E))
            {
                if (actuallyBeLikeEditorStyleMovement)
                {
                    delta3.y += delta;
                } else
                {
                    angles.y += deltaRot;
                }
            }
            if (Input.GetKey(KeyCode.Q))
            {
                if (actuallyBeLikeEditorStyleMovement)
                {
                    delta3.y -= delta;
                } else
                {
                    angles.y -= deltaRot;
                }
            }
            if (Input.GetKey(KeyCode.R))
            {
                angles.x += deltaRot;
            }
            if (Input.GetKey(KeyCode.F))
            {
                angles.x -= deltaRot;
            }

            // Apply to the node. Note that unity translates in local space, by default.
            this.gameObject.transform.Translate(delta3);

            this.gameObject.transform.RotateAround(this.gameObject.transform.position, Vector3.up, angles.y);

            Vector3 xAxis = this.gameObject.transform.TransformDirection(-Vector3.right);
            this.gameObject.transform.RotateAround(this.gameObject.transform.position, xAxis, angles.x);
        }
    }
}
