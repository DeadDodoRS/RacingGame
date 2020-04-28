﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BehaviourAI
{
    public enum Sides
    {
        LEFT,
        RIGHT,
    }

    public class PathSegment
    {
        public WayPoint Waypoint { get; }
        public Vector3 CarRacingPoint { get; }

        public PathSegment(DriverAI driver, WayPoint wp)
        {
            Waypoint = wp;
            
            //TODO: Get real car width
            float carWidth = 2f;

            float newWpWidth = wp.Width - carWidth;

            float driverTargetPoint = !driver.IsAttack ? wp.LocalFinalRacingPoint : wp.LocalOvertakeRacingPoint;

            //Lock local racing point in new waypoint width
            float newLocalRp = Mathf.Clamp(driverTargetPoint, -newWpWidth / wp.Width / 2, newWpWidth / wp.Width / 2);

            CarRacingPoint = wp.transform.TransformPoint(newLocalRp * wp.Width, 0.5f, 0);
        }
    }
    
    public class RacingState : BaseState
    {
        private RaceCircuit _circuit;

        private float _steerInput;
        private float _accelerationInput;

        private WayPoint _currentWaypoint;
        private int _currentWaypointIndex;

        private List<PathSegment> _carPath;

        //Turning
        private Vector3 _turningTargetPoint;
        private float _turningTargetDistance = 10f;

        //Braking
        private Vector3 _brakingPoint;
        
        private float _nextCornerAngle;
        private float _cornerTargetSpeed;
        private float _brakingDistance;
        
        //Waypoints
        private Sides _carWaypointApproachSide;

        public RacingState(RaceCircuit circuit, DriverAI ai, BaseCar car) : base(ai, car)
        {
            _circuit = circuit;

            _currentWaypointIndex = 0;
            _currentWaypoint = _circuit.GetWaypointByIndex(_currentWaypointIndex);
        }

        public override void OnDrawGizmos()
        {
            base.OnDrawGizmos();

            if (_carPath == null)
                return;

            Gizmos.color = Color.green;
            foreach(PathSegment point in _carPath)
                Gizmos.DrawSphere(point.CarRacingPoint, 0.25f);
            
            Gizmos.color = new Color(0, 0.3f, 0);
            Gizmos.DrawLine(Car.transform.position, _carPath[0].CarRacingPoint);
            Gizmos.DrawRay(_carPath[0].CarRacingPoint, Car.transform.forward * 20);
            
            Gizmos.DrawRay(_carPath[0].CarRacingPoint, _carPath[_carPath.Count - 1].CarRacingPoint - _carPath[0].CarRacingPoint);

            //Target turning point
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(_turningTargetPoint, 0.2f);
            
            //Braking point
            if (_brakingDistance > 1)
            {
                Gizmos.color = Color.red;
                Vector3 brakingPosition = _carPath[0].CarRacingPoint + (Car.transform.position - _carPath[0].CarRacingPoint).normalized * _brakingDistance;
                Gizmos.DrawLine(brakingPosition - Car.transform.right * 3, brakingPosition + Car.transform.right * 3);
                Handles.Label(brakingPosition - Car.transform.right * 3, $"Braking: {Math.Round(_brakingDistance, 1)}");
            }

            //Corner info
            string cornerInfo = $"ang: {Math.Round(_nextCornerAngle, 1)}°{Environment.NewLine}" +
                                $"tSpeed: {Math.Round(_cornerTargetSpeed, 1)}";
            
            Handles.Label(_carPath[0].CarRacingPoint, cornerInfo);
        }

        public override void FixedUpdate()
        {
            base.FixedUpdate();
            
            Car.SetSteerAngle(_steerInput);
            Car.SetMotorTorque(Mathf.Clamp(_accelerationInput, 0, 1));
            Car.SetBrakeTorque(Mathf.Clamp(-_accelerationInput, 0, 1));
        }
        
        public override void OnUpdate()
        {
            base.OnUpdate();
            
            //Steer analyst
            AnalysisWaypoints();

            UpdateCurrentWaypoint();
            UpdateTurningTarget();
            
            //Calculate inputs
            
            _steerInput = CalculateSteeringInput();
            _accelerationInput = CalculateMoveInput();
        }

        private float CalculateSteeringInput()
        {
            float steerAngleToTracker = GetAngleToBetweenTransfors(Car.transform, _turningTargetPoint);
            
//            //FROM BASE STATE: уворот
//
//            if (_frontSensors[0].IsDetected)
//                steerAngleToTracker += Driver.Car.MaxWheelAngle * 0.33f;
//            else
//                steerAngleToTracker -= Driver.Car.MaxWheelAngle * 0.33f;
//
//            if (_frontSensors[2].IsDetected)
//                steerAngleToTracker -= Driver.Car.MaxWheelAngle * 0.33f;
//            else
//                steerAngleToTracker += Driver.Car.MaxWheelAngle * 0.33f;
//
//            //end 

            return Mathf.Lerp(Driver.Car.CurrentWheelAngle, steerAngleToTracker, Time.deltaTime * Driver.WheelAngleSpeed);
        }

        private float CalculateMoveInput()
        {
            //Move input analyst
            _nextCornerAngle = Vector3.Angle(Car.transform.forward * 20, _carPath[_carPath.Count - 1].CarRacingPoint - _carPath[0].CarRacingPoint);
            float distanceToCorner = Vector3.Distance(Car.transform.position, _carPath[0].CarRacingPoint);

            _cornerTargetSpeed = Driver.SpeedByCornerAnlge.Evaluate(_nextCornerAngle);
            _brakingDistance = Driver.BrakingDistanceByDeltaSpeed.Evaluate(Mathf.Clamp(Driver.Car.CarSpeed - _cornerTargetSpeed, 0, float.MaxValue));
            
            float targetMoveInput = distanceToCorner > _brakingDistance? 1f: -1f;
            
            //Create pedal input smooth
            float pedalsInputSpeed = 10;
            
            //Create rubberbanding acceleration lag :
            if (_accelerationInput > 0 && targetMoveInput > _accelerationInput)
            {
                pedalsInputSpeed *= Driver.RubberBandingValue * Driver.RubberBandingAccelerationSpeedMultiplyer;
            }
            
            //Set smooth pedal input
            float newInput = Mathf.Lerp(_accelerationInput, targetMoveInput, Time.deltaTime * pedalsInputSpeed);
            return Mathf.Clamp(newInput, -1, 1);
        }

        private void AnalysisWaypoints()
        {
            //TODO: Remove another direction turn from list
            List<WayPoint> wpList = _circuit.GetWaypointsInDistance(Car.transform.position, 35, _currentWaypointIndex);

            _carPath = new List<PathSegment>();
            foreach (WayPoint wp in wpList)
                _carPath.Add(new PathSegment(Driver, wp));
        }

        private void UpdateCurrentWaypoint()
        {
            if (_carWaypointApproachSide != GetCarWaypointApproachSide(_currentWaypoint))
            {
                _currentWaypointIndex++;
                _currentWaypoint = _circuit.GetWaypointByIndex(_currentWaypointIndex);
                
                _carWaypointApproachSide = GetCarWaypointApproachSide(_currentWaypoint);
            }
        }

        //Возвращает сторону с которой автомобиль приближается к Waypoint
        //Вычисление со стороны правого края дороги
        private Sides GetCarWaypointApproachSide(WayPoint waypoint)
        {
            Vector3 waypointLineVector = waypoint.LeftBorder - waypoint.RightBorder;
            Vector3 toCarWaypointVector = Driver.transform.position + Driver.transform.forward * 2.5f - waypoint.RightBorder;
            float singledAngle = Vector3.SignedAngle(waypointLineVector, toCarWaypointVector, Vector3.up);
            
            return singledAngle < 0 ? Sides.LEFT : Sides.RIGHT;
        }

        private void UpdateTurningTarget()
        {
            float distance = _turningTargetDistance;
            
            Vector3 pointFrom = Driver.transform.position;
            Vector3 pointTo = _carPath[0].CarRacingPoint;

            for (int i = 0; i < _carPath.Count - 1; i++)
            {
                float distToNextTarget = (pointFrom - pointTo).magnitude;
                
                if (distance < distToNextTarget)
                {
                    break;
                }
                else
                {
                    distance -= distToNextTarget;
                    pointFrom = _carPath[i].CarRacingPoint;
                    pointTo = _carPath[i + 1].CarRacingPoint;
                }
            }

            _turningTargetPoint = pointFrom + (pointTo - pointFrom).normalized * distance;
        }

        private float GetTurnMileage()
        {
//            float distance = 0;
//
//            //If is simple corner (only two points => не учитывать длину поворота)
//            if (_analysisWaypoints.Count <= 2)
//                return 0;
//
//            for(int i = 0; i < _analysisWaypoints.Count - 1; i++)
//                distance += Vector3.Distance(_analysisWaypoints[i].transform.position, _analysisWaypoints[i + 1].transform.position);
//            
//            return distance;
            return 0;
        }

        //DUPLICATE from chase state
        private float GetAngleToBetweenTransfors(Transform from, Vector3 to)
        {
            Vector3 deltaVec = from.InverseTransformPoint(to);
            return Mathf.Atan2(deltaVec.x, deltaVec.z) * Mathf.Rad2Deg;
        }
    }
}
