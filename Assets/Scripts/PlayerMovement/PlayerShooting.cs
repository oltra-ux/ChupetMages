using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using System.Linq;
using Utilities;
using System;
using Unity.Netcode.Components;
using Kart;

public struct ShootPayload : INetworkSerializable
{
    public int tick;
    public ulong shooterId;
    public Vector3 shotOrigin;
    public Vector3 shotDirection;
    public DateTime timestamp;  // Para calcular la latencia

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref tick);
        serializer.SerializeValue(ref shooterId);
        serializer.SerializeValue(ref shotOrigin);
        serializer.SerializeValue(ref shotDirection);
        serializer.SerializeValue(ref timestamp);
    }
}
[System.Serializable]
public class Spell
{
    public enum SpellType { Hitscan, Projectile }
    public SpellType spellType;
    public float delayBetweenSpells = 0.5f; // Delay entre disparos de hechizos
    public float projectileSpeed = 20f;     // Velocidad del proyectil
    // Otras propiedades del hechizo como daño, efectos, etc.
}

public class PlayerShooting : NetworkBehaviour
{
    [SerializeField] List<Spell> spells;  // Lista de hechizos configurados
    private int currentSpellIndex = 0;    // Índice del hechizo actual
    private bool isFiring = false;        // Control para saber si el jugador está disparando
    private Coroutine firingCoroutine;    // Referencia a la corrutina de disparo


    [SerializeField] LayerMask hitMask;
    [SerializeField] float shotRange = 100f;
    [SerializeField] GameObject hitEffect;
    [SerializeField] Transform cam;
    [SerializeField] Transform firePoint;
    [SerializeField] GameObject projectilePrefab;

    [Header("Network General")]
    NetworkTimer timer;
    const float k_serverTickRate = 60f;
    const int k_bufferSize = 1024;
    CircularBuffer<StatePayload> clientStateBuffer;
    CircularBuffer<ShootPayload> shootBuffer;

    void Start()
    {
        timer = new NetworkTimer(k_serverTickRate);
        clientStateBuffer = new CircularBuffer<StatePayload>(k_bufferSize);
        shootBuffer = new CircularBuffer<ShootPayload>(k_bufferSize);
    }

    void Update()
    {
        if (IsOwner && Input.GetButtonDown("Fire1") && !isFiring)
        {
            // Iniciar la secuencia de disparo
            isFiring = true;
            firingCoroutine = StartCoroutine(Fire());
        }

        if (IsOwner && Input.GetButtonUp("Fire1"))
        {
            // Detener la secuencia si se suelta el botón
            isFiring = false;
            if (firingCoroutine != null) StopCoroutine(firingCoroutine);
        }
    }

    IEnumerator Fire()
    {
        while (isFiring)
        {
            Spell currentSpell = spells[currentSpellIndex];

            if (currentSpell.spellType == Spell.SpellType.Hitscan)
            {
                FireHitscan();
            }
            else if (currentSpell.spellType == Spell.SpellType.Projectile)
            {
                FireProjectile(currentSpell);
            }

            // Mover al siguiente hechizo en la lista
            currentSpellIndex = (currentSpellIndex + 1) % spells.Count;

            // Esperar el delay configurado entre hechizos
            yield return new WaitForSeconds(currentSpell.delayBetweenSpells);
        }
    }

    void FireHitscan()
    {
        // Utilizar la posición de la cámara y su dirección
        Vector3 shotOrigin = cam.position;
        Vector3 shotDirection = cam.forward;

        ShootPayload shootPayload = new ShootPayload()
        {
            tick = timer.currentTick,
            shooterId = NetworkObjectId,
            shotOrigin = shotOrigin,
            shotDirection = shotDirection,
            timestamp = DateTime.Now
        };

        // Enviar el disparo al servidor
        ShootServerRpc(shootPayload);
    }
    void FireProjectile(Spell spell)
    {
        Vector3 shotOrigin = firePoint.position;
        Vector3 shotDirection = cam.forward;;

        ShootPayload shootPayload = new ShootPayload()
        {
            tick = timer.currentTick,
            shooterId = NetworkObjectId,
            shotOrigin = shotOrigin,
            shotDirection = shotDirection,
            timestamp = DateTime.Now
        };

        SpawnProjectileServerRpc(shootPayload);
    }
    [ServerRpc]
    void SpawnProjectileServerRpc(ShootPayload shootPayload)
    {
        // Asegurarse de que el servidor spawnee el proyectil
        Vector3 projOrigin = shootPayload.shotOrigin;
        Quaternion shotRotation = Quaternion.LookRotation(shootPayload.shotDirection);

        GameObject projectile = Instantiate(projectilePrefab, projOrigin, shotRotation);
        NetworkObject projectileNetObj = projectile.GetComponent<NetworkObject>();
        projectileNetObj.Spawn();  // Solo el servidor puede spawnear el proyectil en la red
    }
    [ClientRpc]
    void SpawnProjectileClientRpc(Vector3 origin, Vector3 direction)
    {
        Vector3 projOrigin = origin;
        Quaternion shotRotation = Quaternion.LookRotation(transform.forward);

        GameObject projectile = Instantiate(projectilePrefab, projOrigin, shotRotation);
        NetworkObject projectileNetObj = projectile.GetComponent<NetworkObject>();
        projectileNetObj.Spawn();
    }

    [ServerRpc]
    void ShootServerRpc(ShootPayload shootPayload)
    {
        // Retroceso temporal para compensar el lag
        int bufferIndex = ((shootPayload.tick - CalculateLatencyInTicks(shootPayload)) + k_bufferSize) % k_bufferSize;
        StatePayload rewindState = clientStateBuffer.Get(bufferIndex);

        // Calcular el punto de impacto retrocediendo en el tiempo
        Ray ray = new Ray(shootPayload.shotOrigin, shootPayload.shotDirection);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, shotRange, hitMask))
        {
            // Impacto detectado
            Debug.Log($"Player {shootPayload.shooterId} hit {hit.collider.name} at position {hit.point}");
            
            // Aplicar efectos o daño
            HitClientRpc(hit.point, hit.normal, shootPayload.shooterId);
        }
    }

    [ClientRpc]
    void HitClientRpc(Vector3 hitPoint, Vector3 hitNormal, ulong shooterId)
    {
        // Mostrar efecto de impacto en los clientes
        Instantiate(hitEffect, hitPoint, Quaternion.LookRotation(hitNormal));
    }

    int CalculateLatencyInTicks(ShootPayload shootPayload)
    {
        float latencySeconds = (float)(DateTime.Now - shootPayload.timestamp).TotalSeconds;
        return Mathf.CeilToInt(latencySeconds * k_serverTickRate);
    }
}